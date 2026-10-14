import type { CurrentStoryDto, StoryVersionDto } from '@shared/api';
import { formatAddress, parseAddress } from '@shared/util/segment-address';
import type { Attestation, QualityHint, StoryDraftWire, StorySnapshot } from '../domain/story.model';

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
const MESSAGE_KEY_PATTERN = /^[A-Za-z0-9._:-]+$/;
/** `02.3.4-T1` bounds one hint's evidence at 200 chars: a longer value is truncated, never dropped. */
const MAX_HINT_EVIDENCE = 200;
/** The server list is unbounded; only the first 200 rows are considered (DoS bound). */
const MAX_QUALITY_HINTS = 200;

/**
 * Cut at the cap on a code-point boundary: an astral character is one UTF-16 *pair*, so a plain `slice` can end on a
 * lone high surrogate and the rendered evidence becomes an unpaired replacement glyph. When the pair straddles the
 * cap, the high surrogate at `cap - 1` is dropped with it (199 chars, never 200 with half a character).
 */
function truncateEvidence(evidence: string, cap = MAX_HINT_EVIDENCE): string {
  if (evidence.length <= cap) return evidence;
  const last = evidence.charCodeAt(cap - 1);
  return evidence.slice(0, last >= 0xd800 && last <= 0xdbff ? cap - 1 : cap);
}

/**
 * One hint row → `QualityHint`, or `null` when the row is malformed and must be dropped. A bad row never throws
 * (STATUS 'Hint mapper strictness C7'): throwing here would turn the whole episode detail into a 502. An unknown
 * `severity`/`kind` is neutralised (`advisory`/`taste`) so the hint is still shown, and an unparseable segment keeps
 * its hint with `segment: null`.
 */
function mapHint(dto: unknown): QualityHint | null {
  if (typeof dto !== 'object' || dto === null || Array.isArray(dto)) return null;
  const source = dto as Readonly<Record<string, unknown>>;
  const ruleId = source['ruleId'];
  const messageKey = source['messageKey'];
  const evidence = source['evidence'];
  const repairDirection = source['repairDirection'];
  const segment = source['segment'];
  if (typeof ruleId !== 'string' || ruleId.length > 64 || !MESSAGE_KEY_PATTERN.test(ruleId)) return null;
  if (typeof messageKey !== 'string' || messageKey.length > 128 || !MESSAGE_KEY_PATTERN.test(messageKey)) return null;
  if (typeof evidence !== 'string') return null;
  if (repairDirection !== null && repairDirection !== undefined && typeof repairDirection !== 'string') return null;
  const address = typeof segment === 'string' ? parseAddress(segment) : null;
  return {
    ruleId,
    severity: source['severity'] === 'blocking' ? 'blocking' : 'advisory',
    kind: source['kind'] === 'objective' ? 'objective' : 'taste',
    segment: address === null ? null : formatAddress(address.kind, address.index ?? undefined),
    messageKey,
    evidence: truncateEvidence(evidence),
    repairDirection: typeof repairDirection === 'string' ? truncateEvidence(repairDirection, 300) : null,
  };
}

function mapHints(hints: readonly unknown[]): readonly QualityHint[] {
  const mapped: QualityHint[] = [];
  for (const hint of hints.slice(0, MAX_QUALITY_HINTS)) {
    const value = mapHint(hint);
    if (value !== null) mapped.push(value);
  }
  return mapped;
}

/**
 * The true-crime attestation of the response, or `null` when the wire carries none (`StoryVersionDto` never has one, a
 * plain `null` is a story without a block). Fail closed like the content: a half-readable attestation is the gate for
 * approve, so a malformed one is a 502 rather than a silently missing precondition.
 */
function mapAttestation(dto: CurrentStoryDto | StoryVersionDto): Attestation | null {
  if (!('attestation' in dto)) return null;
  const raw: unknown = dto.attestation;
  if (raw === null || raw === undefined) return null;
  if (typeof raw !== 'object' || Array.isArray(raw)) throw new StoryMappingError();
  const required: unknown = 'required' in raw ? raw.required : undefined;
  const textVersion: unknown = 'textVersion' in raw ? raw.textVersion : undefined;
  const text: unknown = 'text' in raw ? raw.text : undefined;
  const acceptedAt: unknown = 'acceptedAt' in raw ? raw.acceptedAt : undefined;
  if (typeof required !== 'boolean' || typeof textVersion !== 'string') throw new StoryMappingError();
  if (text !== null && text !== undefined && typeof text !== 'string') throw new StoryMappingError();
  if (acceptedAt !== null && acceptedAt !== undefined && typeof acceptedAt !== 'string') throw new StoryMappingError();
  return { required, textVersion, text: text ?? null, acceptedAt: acceptedAt ?? null };
}

export function mapStorySnapshot(dto: CurrentStoryDto | StoryVersionDto, responseEtag?: string | null): StorySnapshot {
  const content: unknown = dto.content;
  const version = Number(dto.version), wordCount = Number(dto.wordCount);
  const attestation = mapAttestation(dto);
  if (!isStoryContent(content) || !Number.isSafeInteger(version) || version < 1 || !Number.isSafeInteger(wordCount) || wordCount < 0 ||
      typeof dto.versionId !== 'string' || typeof dto.etag !== 'string' || typeof dto.source !== 'string' || !Array.isArray(dto.qualityHints)) throw new StoryMappingError();
  return {
    versionId: dto.versionId, version, etag: responseEtag ?? dto.etag, source: dto.source, wordCount,
    content, qualityHints: mapHints(dto.qualityHints), attestation,
  };
}
