import type { StoryVersionDetailDto, StoryVersionSummaryDto } from '@shared/api';
import { StoryMappingError } from './story.mapper';
import { mapVersionDetail, mapVersionSummary } from './story-version.mapper';

const CONTENT = {
  title: 'The lantern', hook: { text: 'A light appeared.', keywords: ['lantern'], fact_refs: [] },
  paragraphs: [{ beat: 'beat-0', text: 'Paragraph 1 waits.', keywords: ['road'], fact_refs: [] }],
  ending: { text: 'The light vanished.', keywords: ['dawn'], fact_refs: [] },
  description: 'A midnight story', tags: ['night'], content_warnings: ['fear'], structure: { beats: [1, 2] },
};

function summaryDto(overrides: Partial<StoryVersionSummaryDto> = {}): StoryVersionSummaryDto {
  return {
    versionId: 'v1', version: 1, source: 'generated', createdAt: '2026-10-01T10:00:00Z',
    wordCount: 120, etag: '"sv-v1"', similarityScore: null, approved: false, isCurrent: false, ...overrides,
  };
}

function detailDto(overrides: Partial<StoryVersionDetailDto> = {}): StoryVersionDetailDto {
  return {
    versionId: 'v1', version: 1, source: 'generated', createdAt: '2026-10-01T10:00:00Z', wordCount: 120,
    etag: '"sv-v1"', similarityScore: null, auditReport: null, body: 'Plain body.', content: CONTENT,
    createdBy: 'owner@example.com', factSheetId: null, keywords: ['lantern'], qualityHints: [], ...overrides,
  };
}

describe('mapVersionSummary', () => {
  it('maps a generated summary and coerces numeric strings', () => {
    const summary = mapVersionSummary(summaryDto({ version: '3', wordCount: '250', similarityScore: '0.75' }));
    expect(summary).toEqual({
      versionId: 'v1', version: 3, source: 'generated', createdAt: '2026-10-01T10:00:00Z',
      wordCount: 250, etag: '"sv-v1"', similarityScore: 0.75, approved: false, isCurrent: false,
    });
  });

  it('keeps a nullable similarityScore null', () => {
    expect(mapVersionSummary(summaryDto({ similarityScore: null })).similarityScore).toBeNull();
  });

  it('falls back to other for an unknown source value', () => {
    expect(mapVersionSummary(summaryDto({ source: 'restored' })).source).toBe('other');
    expect(mapVersionSummary(summaryDto({ source: 'edited' })).source).toBe('edited');
  });

  it('rejects a version below 1 or a non-finite similarity', () => {
    expect(() => mapVersionSummary(summaryDto({ version: 0 }))).toThrow(StoryMappingError);
    expect(() => mapVersionSummary(summaryDto({ version: 'x' }))).toThrow(StoryMappingError);
    expect(() => mapVersionSummary(summaryDto({ similarityScore: 'NaN' }))).toThrow(StoryMappingError);
  });

  it('rejects a missing identifier', () => {
    expect(() => mapVersionSummary({ ...summaryDto(), versionId: null as unknown as string })).toThrow(StoryMappingError);
  });
});

describe('mapVersionDetail', () => {
  it('maps the raw story.v1 content and the author', () => {
    const detail = mapVersionDetail(detailDto());
    expect(detail.body).toBe('Plain body.');
    expect(detail.createdBy).toBe('owner@example.com');
    expect(detail.content).toEqual(CONTENT);
  });

  it('leaves approved/isCurrent false - the detail route does not carry them', () => {
    const detail = mapVersionDetail(detailDto());
    expect(detail.approved).toBe(false);
    expect(detail.isCurrent).toBe(false);
  });

  it('fails closed to null content when the stored object is not story.v1', () => {
    expect(mapVersionDetail(detailDto({ content: { not: 'story' } })).content).toBeNull();
    expect(mapVersionDetail(detailDto({ content: null })).content).toBeNull();
  });

  it('rejects a non-string body', () => {
    expect(() => mapVersionDetail({ ...detailDto(), body: null as unknown as string })).toThrow(StoryMappingError);
  });
});
