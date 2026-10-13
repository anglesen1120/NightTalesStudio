import type { StoryVersionDetailDto, StoryVersionSummaryDto } from '@shared/api';
import type { StoryVersionDetail, StoryVersionSummary, VersionSource } from '../domain/story-version.model';
import { isStoryContent, StoryMappingError } from './story.mapper';

/** The fields both version DTOs carry; the list route adds `approved`/`isCurrent`, the detail route does not. */
interface VersionHeaderWire {
  readonly versionId: string;
  readonly version: number | string;
  readonly source: string;
  readonly createdAt: string;
  readonly wordCount: number | string;
  readonly etag: string;
  readonly similarityScore: number | string | null;
}

interface VersionHeader {
  readonly versionId: string;
  readonly version: number;
  readonly source: VersionSource;
  readonly createdAt: string;
  readonly wordCount: number;
  readonly etag: string;
  readonly similarityScore: number | null;
}

/** Integer wire fields arrive as a number or a numeric string (ng-openapi-gen `(number | string)`). */
function mapCount(value: number | string, minimum: number): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum) throw new StoryMappingError();
  return parsed;
}

/** `similarityScore` is nullable; any other non-finite value is a mapping failure, never a silent 0. */
function mapSimilarity(value: number | string | null): number | null {
  if (value === null) return null;
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) throw new StoryMappingError();
  return parsed;
}

function mapSource(source: string): VersionSource {
  if (source === 'generated' || source === 'revised' || source === 'edited') return source;
  return 'other';
}

function mapHeader(dto: VersionHeaderWire): VersionHeader {
  if (typeof dto.versionId !== 'string' || typeof dto.createdAt !== 'string' || typeof dto.etag !== 'string' ||
      typeof dto.source !== 'string') {
    throw new StoryMappingError();
  }
  return {
    versionId: dto.versionId,
    version: mapCount(dto.version, 1),
    source: mapSource(dto.source),
    createdAt: dto.createdAt,
    wordCount: mapCount(dto.wordCount, 0),
    etag: dto.etag,
    similarityScore: mapSimilarity(dto.similarityScore),
  };
}

export function mapVersionSummary(dto: StoryVersionSummaryDto): StoryVersionSummary {
  if (typeof dto.approved !== 'boolean' || typeof dto.isCurrent !== 'boolean') throw new StoryMappingError();
  return { ...mapHeader(dto), approved: dto.approved, isCurrent: dto.isCurrent };
}

/**
 * Detail adds the plain-text body, the raw story.v1 content (fail-closed to `null`) and the author. `approved` and
 * `isCurrent` are NOT on the detail route: they are filled with `false` here and `VersionsStore` substitutes the
 * authoritative values from the list row it fetched alongside (`VersionsStore.loadSide`), which is also what `canRestore`
 * and the badges read.
 */
export function mapVersionDetail(dto: StoryVersionDetailDto): StoryVersionDetail {
  if (typeof dto.body !== 'string' || typeof dto.createdBy !== 'string') throw new StoryMappingError();
  const content: unknown = dto.content;
  return {
    ...mapHeader(dto),
    approved: false,
    isCurrent: false,
    body: dto.body,
    createdBy: dto.createdBy,
    content: isStoryContent(content) ? content : null,
  };
}
