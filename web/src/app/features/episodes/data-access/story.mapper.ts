import type { CurrentStoryDto, StoryVersionDto } from '@shared/api';
import { formatAddress, parseAddress } from '@shared/util/segment-address';
import { REVISION_DRIFT_TYPE, SCORE_DIMENSIONS } from '../domain/audit.model';
import type { AuditReport, ModelReview, ReviewIssue, RevisionInfo, ScoreDimension, SimilaritySection, SimilaritySegment } from '../domain/audit.model';
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

/**
 * The server list is unbounded; only the first 200 rows of a review list are considered (DoS bound), like the hints.
 */
const MAX_REVIEW_ISSUES = 200;
/** `02.5.6-T1` sends at most the ten highest-sharing segments, descending; the client keeps that bound. */
const MAX_SIMILARITY_SEGMENTS = 10;
/** The longest issue `type` kept; a longer one is cut, never dropped. */
const MAX_REVIEW_TYPE = 64;
/** The longest `revision.reason` code kept (`[a-z0-9_]` server-side). */
const MAX_REVISION_REASON = 64;
/** The impact and the repair direction are secondary prose: bounded like the hints' repair direction (`02.3.4-T1`). */
const MAX_REVIEW_PROSE = 300;

/**
 * `audit.v1` is stored verbatim (`02.3.4-T2:34`) and read here as plain JSON: every block is an object or reads as
 * absent. Unlike the strict `mapHint`/content paths, nothing in the audit mapping throws — a malformed audit part is
 * `null`/empty so it can never turn the episode detail read into a 502 (soft `02.6.2-T1`).
 */
function record(value: unknown): Readonly<Record<string, unknown>> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Readonly<Record<string, unknown>> : null;
}

/** A review id is used as a DOM id and an i18n key elsewhere, so only this exact shape passes through verbatim. */
const SAFE_ISSUE_ID_PATTERN = /^[A-Za-z0-9._:-]{1,64}$/;

/**
 * One `model_review.issues[]` row for the expanded "why" panel. A row that is not an object is dropped; a row that is
 * one is kept even when fields are missing (its evidence is what the panel is for), with an unsafe id blanked and every
 * string bounded. The severity of a taste issue is clamped to `advisory` here (`02.6.1-T1:99`), the one place the wire
 * value is normalised for the whole client.
 */
function mapReviewIssue(row: unknown): ReviewIssue | null {
  const source = record(row);
  if (source === null) return null;
  const id = source['id'];
  const type = source['type'];
  const segment = source['segment'];
  const impact = source['impact'];
  const evidence = source['evidence'];
  const repairDirection = source['repair_direction'] ?? source['repairDirection'];
  const address = typeof segment === 'string' ? parseAddress(segment) : null;
  const kind = source['kind'] === 'objective' ? 'objective' : 'taste';
  return {
    id: typeof id === 'string' && SAFE_ISSUE_ID_PATTERN.test(id) ? id : '',
    type: typeof type === 'string' ? truncateEvidence(type, MAX_REVIEW_TYPE) : '',
    kind,
    severity: source['severity'] === 'blocking' && kind === 'objective' ? 'blocking' : 'advisory',
    segment: address === null ? null : formatAddress(address.kind, address.index ?? undefined),
    impact: typeof impact === 'string' ? truncateEvidence(impact, MAX_REVIEW_PROSE) : null,
    evidence: typeof evidence === 'string' ? truncateEvidence(evidence) : '',
    repairDirection: typeof repairDirection === 'string' ? truncateEvidence(repairDirection, MAX_REVIEW_PROSE) : null,
  };
}

/** A missing or non-array `issues` reads as "none recorded": the panel shows its empty state, never a guessed row. */
function mapIssues(value: unknown): readonly ReviewIssue[] {
  if (!Array.isArray(value)) return [];
  const issues: ReviewIssue[] = [];
  for (const row of value.slice(0, MAX_REVIEW_ISSUES)) {
    const issue = mapReviewIssue(row);
    if (issue !== null) issues.push(issue);
  }
  return issues;
}

/** The seven rubric scores; `null` when the payload carries no `scores` object at all, and per-dimension when absent. */
function mapScores(value: unknown): Readonly<Record<ScoreDimension, number | null>> | null {
  const source = record(value);
  if (source === null) return null;
  const scores: Record<ScoreDimension, number | null> = {
    hook: null, pacing: null, atmosphere: null, natural_language: null, originality: null, factual_care: null, length_fit: null,
  };
  for (const dimension of SCORE_DIMENSIONS) {
    const score = source[dimension];
    if (typeof score === 'number' && Number.isFinite(score)) scores[dimension] = score;
  }
  return scores;
}

/**
 * The review panel's current state, derived exactly as `StoryReadProjection.Review` does (D22): an applied revision's
 * review #2 (`revision.review_after`, which must carry both `scores` and `issues`) is the current state and wins over
 * `model_review`; `stale` always comes from the `model_review` block, the only one carrying the flag.
 */
function mapModelReview(audit: Readonly<Record<string, unknown>>): ModelReview | null {
  const review = record(audit['model_review']);
  if (review === null) return null;
  const revision = record(audit['revision']);
  const after = record(revision !== null && revision['applied'] === true ? revision['review_after'] : undefined);
  const source = after !== null && record(after['scores']) !== null && Array.isArray(after['issues']) ? after : review;
  return { stale: review['stale'] !== false, scores: mapScores(source['scores']), issues: mapIssues(source['issues']) };
}

/** `StoryReadProjection.Drift`: the `revision_drift` issue of review #2, or any set flag of the additive `revision.drift`. */
function hasRevisionDrift(revision: Readonly<Record<string, unknown>>): boolean {
  const after = record(revision['review_after']);
  const afterIssues = after === null ? null : after['issues'];
  if (Array.isArray(afterIssues) && afterIssues.some((row) => record(row)?.['type'] === REVISION_DRIFT_TYPE)) return true;
  const drift = record(revision['drift']);
  if (drift === null) return false;
  if (drift['segment_count_changed'] === true) return true;
  return ['changed_segments', 'unflagged_changed', 'added_findings'].some((name) => {
    const items = drift[name];
    return Array.isArray(items) && items.length > 0;
  });
}

/** The revision block, or `null` when the payload carries none (soft `02.6.2-T1`: no badge, no drift note, no error). */
function mapRevision(audit: Readonly<Record<string, unknown>>): RevisionInfo | null {
  const revision = record(audit['revision']);
  if (revision === null) return null;
  const reason = revision['reason'];
  return {
    applied: revision['applied'] === true,
    drift: hasRevisionDrift(revision),
    reason: typeof reason === 'string' ? truncateEvidence(reason, MAX_REVISION_REASON) : null,
  };
}

/** The similarity section; a row without a known address or with a non-positive share is not a paragraph we may mark. */
function mapSimilarity(audit: Readonly<Record<string, unknown>>): SimilaritySection | null {
  const similarity = record(audit['similarity']);
  if (similarity === null) return null;
  const segments: SimilaritySegment[] = [];
  const rows = similarity['segments'];
  if (Array.isArray(rows)) {
    for (const row of rows.slice(0, MAX_SIMILARITY_SEGMENTS)) {
      const source = record(row);
      if (source === null) continue;
      const share = source['share'];
      const segment = source['segment'];
      const address = typeof segment === 'string' ? parseAddress(segment) : null;
      if (address === null || typeof share !== 'number' || !Number.isFinite(share) || share <= 0) continue;
      segments.push({ segment: formatAddress(address.kind, address.index ?? undefined), share });
    }
  }
  const corpus = similarity['corpus'];
  return { score: mapOptionalScore(similarity['score']), corpus: typeof corpus === 'string' ? corpus : null, segments };
}

/** The verbatim `audit.v1` document as the client reads it; `null` when the payload carries none (or no object). */
export function mapAuditReport(raw: unknown): AuditReport | null {
  const audit = record(raw);
  if (audit === null) return null;
  return { modelReview: mapModelReview(audit), revision: mapRevision(audit), similarity: mapSimilarity(audit) };
}

/**
 * `similarityScore` arrives as a number, a numeric string or `null` (`ng-openapi-gen` widens a nullable decimal). Here a
 * value that is neither reads as `null` — the detail read is never failed over a score the UI only displays.
 */
function mapOptionalScore(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value !== 'string' || value.trim() === '') return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

export function mapStorySnapshot(dto: CurrentStoryDto | StoryVersionDto, responseEtag?: string | null): StorySnapshot {
  const content: unknown = dto.content;
  const version = Number(dto.version), wordCount = Number(dto.wordCount);
  const attestation = mapAttestation(dto);
  if (!isStoryContent(content) || !Number.isSafeInteger(version) || version < 1 || !Number.isSafeInteger(wordCount) || wordCount < 0 ||
      typeof dto.versionId !== 'string' || typeof dto.etag !== 'string' || typeof dto.source !== 'string' || !Array.isArray(dto.qualityHints)) throw new StoryMappingError();
  return {
    versionId: dto.versionId, version, etag: responseEtag ?? dto.etag, source: dto.source, wordCount,
    content, qualityHints: mapHints(dto.qualityHints),
    auditReport: 'auditReport' in dto ? mapAuditReport(dto.auditReport) : null,
    similarityScore: 'similarityScore' in dto ? mapOptionalScore(dto.similarityScore) : null,
    attestation,
  };
}
