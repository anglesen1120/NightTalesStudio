import type { CurrentStoryDto, QualityHintDto, StoryVersionDto } from '@shared/api';
import { formatAddress, parseAddress } from '@shared/util/segment-address';
import type { QualityHint, StoryDraftWire, StorySnapshot } from '../domain/story.model';

/** Mapping failure contains no payload; repositories convert it to the shared AppError. */
export class StoryMappingError extends Error {
  constructor() { super('Invalid story response'); this.name = 'StoryMappingError'; }
}

function strings(value: unknown): value is readonly string[] {
  return Array.isArray(value) && value.every((item: unknown) => typeof item === 'string');
}
function isSegment(value: unknown): boolean {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false;
  const segment = value as Partial<StoryDraftWire['hook']>;
  return typeof segment.text === 'string' && strings(segment.keywords) && Array.isArray(segment.fact_refs);
}

/** Fail closed: neither a missing story.v1 field nor an incompatible segment becomes an empty editor. */
export function isStoryContent(value: unknown): value is StoryDraftWire {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false;
  const content = value as Partial<StoryDraftWire>;
  return typeof content.title === 'string' && isSegment(content.hook) && isSegment(content.ending) &&
    Array.isArray(content.paragraphs) && content.paragraphs.every((paragraph: unknown) => {
      if (!isSegment(paragraph)) return false;
      return typeof (paragraph as Partial<StoryDraftWire['paragraphs'][number]>).beat === 'string';
    }) && typeof content.description === 'string' && strings(content.tags) && strings(content.content_warnings) && Object.hasOwn(value, 'structure');
}
function mapHint(dto: QualityHintDto): QualityHint {
  const address = parseAddress(dto.segment);
  if ((dto.severity !== 'advisory' && dto.severity !== 'blocking') || (dto.kind !== 'objective' && dto.kind !== 'taste')) throw new StoryMappingError();
  return {
    ruleId: dto.ruleId, severity: dto.severity, kind: dto.kind,
    segment: address === null ? null : formatAddress(address.kind, address.index ?? undefined),
    messageKey: dto.messageKey, evidence: dto.evidence, repairDirection: dto.repairDirection,
  };
}

export function mapStorySnapshot(dto: CurrentStoryDto | StoryVersionDto, responseEtag?: string | null): StorySnapshot {
  const content: unknown = dto.content;
  const version = Number(dto.version), wordCount = Number(dto.wordCount);
  if (!isStoryContent(content) || !Number.isSafeInteger(version) || version < 1 || !Number.isSafeInteger(wordCount) || wordCount < 0 ||
      typeof dto.versionId !== 'string' || typeof dto.etag !== 'string' || typeof dto.source !== 'string' || !Array.isArray(dto.qualityHints)) throw new StoryMappingError();
  return {
    versionId: dto.versionId, version, etag: responseEtag ?? dto.etag, source: dto.source, wordCount,
    content, qualityHints: dto.qualityHints.map(mapHint),
  };
}
