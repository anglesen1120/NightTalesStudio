import type { FactItemDto, FactSheetDto, FactSheetRunDto, FactSheetSummaryDto, SourceDto, SourceLimitsDto, SourceListItemDto, SourceListDto } from '@shared/api';
import type { Fact, FactConfidence, FactSheet, FactSheetRun, FactSourceRef, ResearchSource, SourceKind, SourceLimits, SourceList, SourceStatus } from '../domain/research.model';

/** Mapping failure contains no payload; the repository converts it to the shared 502 `AppError`. */
export class ResearchMappingError extends Error {
  constructor() { super('Invalid research response'); this.name = 'ResearchMappingError'; }
}

/**
 * A source row as either route answers it: the list adds `isCited`/`errorMessageKey` (`SourceListItemDto`), the write
 * route answers the plain `SourceDto`. Both are the same row for this mapper.
 */
export type SourceRow = SourceDto | SourceListItemDto;

/** Integer wire fields arrive as a number or a numeric string (ng-openapi-gen `(number | string | null)`). */
function intOrNull(value: number | string | null, minimum: number): number | null {
  if (value === null) return null;
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= minimum ? parsed : null;
}

/** A state-bearing count (a version, an id counter): an unusable value fails the mapping instead of inventing one. */
function requiredInt(value: number | string, minimum: number): number {
  const parsed = intOrNull(value, minimum);
  if (parsed === null) throw new ResearchMappingError();
  return parsed;
}

/** A plain decimal number as text (`0.5`, `1`, `1e-2`). Anything else — `0x10`, `Infinity`, `1px` — is not a score. */
const DECIMAL = /^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/;

/**
 * `sourceOverlap` is a score in 0…1 (02 F2), rendered only when present: anything unusable is `null`, never a lie.
 * Unlike the counters it is not an integer.
 */
function scoreOrNull(value: number | string | null): number | null {
  if (value === null) return null;
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  // A blank string is not "0" (`Number('')` is 0) and `Number('0x10')` is 16: neither is a score the wire sent, so a
  // malformed value is dropped rather than turned into a plausible-looking number. A numeric string is still read.
  const text = value.trim();
  if (!DECIMAL.test(text)) return null;
  const parsed = Number(text);
  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * An unknown wire status renders as `failed`: a row that is neither pending nor fetched is done, and `failed` is the
 * one state that offers Retry. Treating it as `pending` would spin forever over a row the server has finished with.
 */
function sourceStatus(value: string): SourceStatus {
  if (value === 'pending' || value === 'fetched' || value === 'failed' || value === 'rejected') return value;
  return 'failed';
}

/**
 * An unknown kind is read from the row itself: the kind only selects a label, so it must never drop a source the server
 * clearly has. A row that carries note text is a note, everything else a link.
 */
function sourceKind(value: string, noteText: string | null): SourceKind {
  if (value === 'url' || value === 'user_note') return value;
  return noteText === null ? 'url' : 'user_note';
}

/** An unknown confidence is advisory-neutral: the badge must never overstate a fact and `low` never blocks (F2/T53). */
function factConfidence(value: string): FactConfidence {
  if (value === 'high' || value === 'medium' || value === 'low') return value;
  return 'medium';
}

function mapLimits(dto: SourceLimitsDto): SourceLimits {
  return {
    maxPerEpisode: requiredInt(dto.maxPerEpisode, 1),
    urlSourcesLast24h: requiredInt(dto.urlSourcesLast24h, 0),
    maxUrlSourcesPerDay: requiredInt(dto.maxUrlSourcesPerDay, 1),
  };
}

function mapFactSourceRef(dto: FactItemDto['sources'][number]): FactSourceRef {
  return { sourceId: dto.sourceId, alias: dto.alias, title: dto.title, url: dto.url, isUserNote: dto.isUserNote === true };
}

/** The wire carries the citation refs, not a separate id list: the ids are those refs, de-duplicated. */
function mapFact(dto: FactItemDto): Fact {
  const sources = dto.sources.map(mapFactSourceRef);
  return {
    id: dto.id,
    statement: dto.statement,
    confidence: factConfidence(dto.confidence),
    sources,
    sourceIds: [...new Set(sources.map((source) => source.sourceId))],
    sourceOverlap: scoreOrNull(dto.sourceOverlap),
    origin: dto.origin,
  };
}

export function mapResearchSource(dto: SourceRow): ResearchSource {
  return {
    id: dto.id,
    kind: sourceKind(dto.kind, dto.noteText),
    url: dto.url,
    registrableDomain: dto.registrableDomain,
    noteText: dto.noteText,
    status: sourceStatus(dto.status),
    errorCode: dto.errorCode,
    // The list answer adds these two members; the write answer (`SourceDto`) has neither.
    errorMessageKey: 'errorMessageKey' in dto ? dto.errorMessageKey : null,
    httpStatus: intOrNull(dto.httpStatus, 100),
    title: dto.title,
    finalUrl: dto.finalUrl,
    contentType: dto.contentType,
    textChars: intOrNull(dto.textChars, 0),
    isCited: 'isCited' in dto && dto.isCited,
    createdAt: dto.createdAt,
    fetchedAt: dto.fetchedAt,
  };
}

export function mapSourceList(dto: SourceListDto): SourceList {
  return { items: dto.items.map(mapResearchSource), limits: mapLimits(dto.limits) };
}

/**
 * `null` in, `null` out: the route answers 200 with a nullable body, so a missing sheet is this state and never an
 * error. The `ETag` response header wins over the body field (the same precedence `mapApprovalOutcome` applies): both
 * describe the version the response just returned, and only the header is guaranteed on a summary answer.
 */
export function mapFactSheet(dto: FactSheetDto | null, responseEtag?: string | null): FactSheet | null {
  if (dto === null) return null;
  const etag = responseEtag ?? dto.etag;
  if (etag === '') throw new ResearchMappingError();
  return {
    id: dto.id,
    version: requiredInt(dto.version, 0),
    // Only the exact `approved` value is approved; every other (or future) wire status stays an editable draft.
    status: dto.status === 'approved' ? 'approved' : 'draft',
    etag,
    facts: dto.facts.map(mapFact),
    promptVersion: dto.promptVersion,
    createdAt: dto.createdAt,
    approvedAt: dto.approvedAt,
    maxFactId: requiredInt(dto.maxFactId, 0),
  };
}

/** The answer of `PUT`/`approve`: the new version's identity. A missing ETag is a mapping failure (502), never a guess. */
export function mapFactSheetWrite(dto: FactSheetSummaryDto, responseEtag?: string | null): { id: string; version: number; status: string; etag: string } {
  const etag = responseEtag ?? dto.etag;
  if (etag === '') throw new ResearchMappingError();
  return { id: dto.id, version: requiredInt(dto.version, 0), status: dto.status, etag };
}

export function mapFactSheetRun(dto: FactSheetRunDto): FactSheetRun {
  return { runId: dto.runId, factSheetId: dto.factSheetId, status: dto.status };
}
