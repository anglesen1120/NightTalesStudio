import type { SourceCandidateDto, SourceCandidateListDto } from '@shared/api';
import type { CandidateStatus, SourceCandidate } from '../domain/source-candidate.model';

/** Mapping failure contains no payload; the repository converts it to the shared 502 `AppError`. */
export class SourceCandidateMappingError extends Error {
  constructor() {
    super('Invalid source candidate response');
    this.name = 'SourceCandidateMappingError';
  }
}

/**
 * An unknown wire `status` reads as `dismissed`: the closed state that offers no action. `proposed` would put Add and
 * Dismiss on a row the server has already finished with (both routes would refuse it), and `added` would claim a source
 * that is in the episode's list when the server never said so. A future server state therefore shows as handled
 * elsewhere — the row keeps its place in the list, and nothing is ever added by a status this client half-understands.
 */
function candidateStatus(value: string): CandidateStatus {
  if (value === 'proposed' || value === 'added' || value === 'dismissed') return value;
  return 'dismissed';
}

/** Integer wire fields arrive as a number or a numeric string (ng-openapi-gen `(number | string)`). */
function rankOf(value: number | string): number {
  const parsed = typeof value === 'number' ? value : Number(value);
  // An unusable rank reads as `0` (unranked) rather than a reason to drop the row: nothing here sorts by rank, and the
  // list keeps the server's own order, so a value the client cannot read never invents a better position.
  return Number.isSafeInteger(parsed) && parsed >= 1 ? parsed : 0;
}

/**
 * One suggestion. `title` and `sourceId` are nullable on the wire and stay nullable here: a null title is a suggestion
 * the row labels with its URL, and a null `sourceId` is a candidate the episode's source list does not carry (yet).
 */
export function mapSourceCandidate(dto: SourceCandidateDto): SourceCandidate {
  // A row without an id cannot be keyed by `@for`, addressed by add/dismiss, or refreshed after a 409. Inventing one
  // would fabricate an id the API refuses, so the read fails as the shared 502 — the same rule the fact-sheet mapper
  // applies to a missing ETag.
  if (dto.id === '') throw new SourceCandidateMappingError();
  return {
    id: dto.id,
    url: dto.url,
    title: dto.title,
    siteName: dto.siteName,
    rank: rankOf(dto.rank),
    status: candidateStatus(dto.status),
    sourceId: dto.sourceId,
    createdAt: dto.createdAt,
  };
}

/** The list answer keeps the server's order: `rank` is informational and never drives a local sort. */
export function mapSourceCandidateList(dto: SourceCandidateListDto): readonly SourceCandidate[] {
  return dto.items.map(mapSourceCandidate);
}
