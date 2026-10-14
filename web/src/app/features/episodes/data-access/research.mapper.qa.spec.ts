import { describe, expect, it } from 'vitest';
import type { FactItemDto, FactSheetDto } from '@shared/api';
import { mapFactSheet } from './research.mapper';

const SOURCE_ID = '22222222-2222-4222-8222-222222222222';

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

function sheetDto(facts: FactItemDto[]): FactSheetDto {
  return {
    id: '33333333-3333-4333-8333-333333333333',
    version: 2,
    status: 'draft',
    etag: '"fs-2"',
    facts,
    promptVersion: 'fact-sheet.v1',
    createdAt: '2026-10-01T10:00:00Z',
    approvedAt: null,
    maxFactId: 1,
  };
}

describe('mapFactSheet wire negatives (QA)', () => {
  it('maps an unknown confidence to medium, never to high or low', () => {
    const sheet = mapFactSheet(sheetDto([factDto({ confidence: 'certain' as unknown as FactItemDto['confidence'] })]));
    expect(sheet?.facts[0]?.confidence).toBe('medium');
  });

  it('maps a blank sourceOverlap to null, not to a real zero score', () => {
    const sheet = mapFactSheet(sheetDto([factDto({ sourceOverlap: '' as unknown as number })]));
    expect(sheet?.facts[0]?.sourceOverlap).toBeNull();
  });

  it('maps a non-numeric sourceOverlap to null', () => {
    const sheet = mapFactSheet(sheetDto([factDto({ sourceOverlap: 'high' as unknown as number })]));
    expect(sheet?.facts[0]?.sourceOverlap).toBeNull();
  });
});
