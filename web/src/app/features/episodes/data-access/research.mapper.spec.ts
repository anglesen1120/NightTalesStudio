import { describe, expect, it } from 'vitest';
import type { FactItemDto, FactSheetDto, FactSheetSummaryDto, SourceDto, SourceListItemDto, SourceListDto } from '@shared/api';
import { mapFactSheet, mapFactSheetRun, mapFactSheetWrite, mapResearchSource, mapSourceList, ResearchMappingError } from './research.mapper';

const SOURCE_ID = '22222222-2222-4222-8222-222222222222';

function sourceRow(overrides: Partial<SourceListItemDto> = {}): SourceListItemDto {
  return {
    id: SOURCE_ID,
    kind: 'url',
    url: 'https://example.com/article',
    registrableDomain: 'example.com',
    noteText: null,
    status: 'fetched',
    errorCode: null,
    errorMessageKey: null,
    httpStatus: 200,
    title: 'An article',
    finalUrl: null,
    contentType: 'text/html',
    textChars: 4200,
    isCited: true,
    createdAt: '2026-10-01T10:00:00Z',
    fetchedAt: '2026-10-01T10:00:05Z',
    ...overrides,
  };
}

function factDto(overrides: Partial<FactItemDto> = {}): FactItemDto {
  return {
    id: 'f1',
    statement: 'Fact 1',
    sources: [{ sourceId: SOURCE_ID, alias: 'S1', title: 'An article', url: 'https://example.com/article', isUserNote: false }],
    sourceOverlap: 0.5,
    origin: 'model',
    confidence: 'high',
    ...overrides,
  };
}

function sheetDto(overrides: Partial<FactSheetDto> = {}): FactSheetDto {
  return {
    id: '33333333-3333-4333-8333-333333333333',
    version: 2,
    status: 'draft',
    etag: '"fs-2"',
    facts: [factDto()],
    promptVersion: 'fact-sheet.v1',
    createdAt: '2026-10-01T10:00:00Z',
    approvedAt: null,
    maxFactId: 1,
    ...overrides,
  };
}

describe('mapResearchSource', () => {
  it('maps a fetched url row with its counters and citation flag', () => {
    expect(mapResearchSource(sourceRow())).toEqual({
      id: SOURCE_ID,
      kind: 'url',
      url: 'https://example.com/article',
      registrableDomain: 'example.com',
      noteText: null,
      status: 'fetched',
      errorCode: null,
      errorMessageKey: null,
      httpStatus: 200,
      title: 'An article',
      finalUrl: null,
      contentType: 'text/html',
      textChars: 4200,
      isCited: true,
      createdAt: '2026-10-01T10:00:00Z',
      fetchedAt: '2026-10-01T10:00:05Z',
    });
  });

  it('reads the counters of a numeric-string wire value', () => {
    const mapped = mapResearchSource(sourceRow({ httpStatus: '404', textChars: '0' }));
    expect(mapped.httpStatus).toBe(404);
    expect(mapped.textChars).toBe(0);
  });

  it('nulls an unusable counter instead of inventing one', () => {
    const mapped = mapResearchSource(sourceRow({ httpStatus: 12, textChars: -5 }));
    expect(mapped.httpStatus).toBeNull();
    expect(mapped.textChars).toBeNull();
  });

  it('renders an unknown status as failed and keeps the failure code', () => {
    expect(mapResearchSource(sourceRow()).status).toBe('fetched');
    expect(mapResearchSource(sourceRow({ status: 'weird' as never })).status).toBe('failed');
    expect(mapResearchSource(sourceRow({ status: 'rejected' })).status).toBe('rejected');
    expect(mapResearchSource(sourceRow({ status: 'failed', errorCode: 'source_unreachable' })).errorCode).toBe('source_unreachable');
  });

  it('reads an unknown kind from the row itself', () => {
    expect(mapResearchSource(sourceRow({ kind: 'user_note' as never, noteText: 'Owner note', url: null })).kind).toBe('user_note');
    expect(mapResearchSource(sourceRow({ kind: 'weird' as never, noteText: null })).kind).toBe('url');
    expect(mapResearchSource(sourceRow({ kind: 'weird' as never, noteText: 'Owner note' })).kind).toBe('user_note');
  });

  it('maps the write answer, which carries neither the citation flag nor a message key', () => {
    const write: SourceDto = {
      id: SOURCE_ID,
      kind: 'url',
      url: 'https://example.com/article',
      normalizedUrl: 'https://example.com/article',
      registrableDomain: 'example.com',
      noteText: null,
      status: 'pending',
      errorCode: null,
      httpStatus: null,
      title: null,
      finalUrl: null,
      contentType: null,
      textChars: null,
      contentSha256: null,
      createdAt: '2026-10-01T10:00:00Z',
      fetchedAt: null,
    };
    const mapped = mapResearchSource(write);
    expect(mapped.status).toBe('pending');
    expect(mapped.isCited).toBe(false);
    expect(mapped.errorMessageKey).toBeNull();
    expect(mapped.httpStatus).toBeNull();
  });
});

describe('mapSourceList', () => {
  it('maps the rows and the limits together', () => {
    const dto: SourceListDto = {
      items: [sourceRow(), sourceRow({ id: 'a', kind: 'user_note', noteText: 'Owner note', url: null, registrableDomain: null, isCited: false })],
      limits: { maxPerEpisode: 10, urlSourcesLast24h: '3', maxUrlSourcesPerDay: 20 },
    };
    const mapped = mapSourceList(dto);
    expect(mapped.items).toHaveLength(2);
    expect(mapped.items[1]!.kind).toBe('user_note');
    expect(mapped.limits).toEqual({ maxPerEpisode: 10, urlSourcesLast24h: 3, maxUrlSourcesPerDay: 20 });
  });

  it('fails the mapping on an unusable limit rather than reporting a wrong cap', () => {
    const dto: SourceListDto = { items: [], limits: { maxPerEpisode: 0, urlSourcesLast24h: 0, maxUrlSourcesPerDay: 20 } };
    expect(() => mapSourceList(dto)).toThrow(ResearchMappingError);
  });
});

describe('mapFactSheet', () => {
  it('keeps a null body as the empty state', () => {
    expect(mapFactSheet(null)).toBeNull();
    expect(mapFactSheet(null, '"fs-9"')).toBeNull();
  });

  it('maps the document, its facts and the citation ids of each fact', () => {
    const mapped = mapFactSheet(sheetDto());
    expect(mapped?.version).toBe(2);
    expect(mapped?.status).toBe('draft');
    expect(mapped?.etag).toBe('"fs-2"');
    expect(mapped?.facts[0]).toEqual({
      id: 'f1',
      statement: 'Fact 1',
      confidence: 'high',
      sources: [{ sourceId: SOURCE_ID, alias: 'S1', title: 'An article', url: 'https://example.com/article', isUserNote: false }],
      sourceIds: [SOURCE_ID],
      sourceOverlap: 0.5,
      origin: 'model',
    });
  });

  it('prefers the response ETag over the body field', () => {
    expect(mapFactSheet(sheetDto(), '"fs-7"')?.etag).toBe('"fs-7"');
    expect(mapFactSheet(sheetDto({ etag: 'W/"fs-2"' }))?.etag).toBe('W/"fs-2"');
  });

  it('fails the mapping when no ETag describes the document', () => {
    expect(() => mapFactSheet(sheetDto({ etag: '' }))).toThrow(ResearchMappingError);
    expect(() => mapFactSheet(sheetDto({ etag: '' }), '')).toThrow(ResearchMappingError);
  });

  it('reads only the exact approved status as approved', () => {
    expect(mapFactSheet(sheetDto({ status: 'approved' }))?.status).toBe('approved');
    expect(mapFactSheet(sheetDto({ status: 'APPROVED' }))?.status).toBe('draft');
    expect(mapFactSheet(sheetDto({ status: 'superseded' }))?.status).toBe('draft');
  });

  it('keeps an unknown confidence as the advisory-neutral medium and nulls an unusable score', () => {
    const mapped = mapFactSheet(sheetDto({ facts: [factDto({ confidence: 'very-high', sourceOverlap: 'x' })] }));
    expect(mapped?.facts[0]!.confidence).toBe('medium');
    expect(mapped?.facts[0]!.sourceOverlap).toBeNull();
  });

  it('nulls a blank overlap instead of reading it as the score zero', () => {
    const facts = mapFactSheet(sheetDto({ facts: [factDto({ sourceOverlap: '' }), factDto({ id: 'f2', sourceOverlap: '   ' })] }))?.facts ?? [];
    expect(facts[0]?.sourceOverlap).toBeNull();
    expect(facts[1]?.sourceOverlap).toBeNull();
  });

  it('reads a numeric string as a score but refuses a non-decimal one', () => {
    const facts = mapFactSheet(sheetDto({ facts: [factDto({ sourceOverlap: '1' }), factDto({ id: 'f2', sourceOverlap: '0x10' })] }))?.facts ?? [];
    expect(facts[0]?.sourceOverlap).toBe(1);
    expect(facts[1]?.sourceOverlap).toBeNull();
  });

  it('de-duplicates the citation ids the wire repeats', () => {
    const ref = { sourceId: SOURCE_ID, alias: 'S1', title: 'An article', url: null, isUserNote: true };
    const facts = mapFactSheet(sheetDto({ facts: [factDto({ sources: [ref, { ...ref, alias: 'S2' }] })] }))?.facts ?? [];
    expect(facts[0]?.sourceIds).toEqual([SOURCE_ID]);
    expect(facts[0]?.sources).toHaveLength(2);
  });

  it('fails the mapping on an unusable version or max fact id', () => {
    expect(() => mapFactSheet(sheetDto({ version: -1 }))).toThrow(ResearchMappingError);
    expect(() => mapFactSheet(sheetDto({ maxFactId: 'x' }))).toThrow(ResearchMappingError);
  });
});

describe('mapFactSheetWrite', () => {
  const summary: FactSheetSummaryDto = {
    id: '33333333-3333-4333-8333-333333333333',
    version: '3',
    status: 'approved',
    etag: '"fs-3"',
    factCount: 2,
    createdAt: '2026-10-01T10:00:00Z',
  };

  it('maps the identity of the version the write installed', () => {
    expect(mapFactSheetWrite(summary)).toEqual({ id: summary.id, version: 3, status: 'approved', etag: '"fs-3"' });
  });

  it('prefers the response ETag and fails without one', () => {
    expect(mapFactSheetWrite(summary, '"fs-4"').etag).toBe('"fs-4"');
    expect(() => mapFactSheetWrite({ ...summary, etag: '' })).toThrow(ResearchMappingError);
  });
});

describe('mapFactSheetRun', () => {
  it('maps the accepted run', () => {
    expect(mapFactSheetRun({ runId: 'run-1', factSheetId: null, status: 'running' })).toEqual({ runId: 'run-1', factSheetId: null, status: 'running' });
  });
});
