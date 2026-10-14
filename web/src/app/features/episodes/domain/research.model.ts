/** Domain types of the episode research page (10.5.5-T1). Wire values are forward-compatible strings where the API enum is open. */

export type SourceKind = 'url' | 'user_note';
export type SourceStatus = 'pending' | 'fetched' | 'failed' | 'rejected';
export type FactConfidence = 'high' | 'medium' | 'low';
/** The wire `status` of a fact-sheet version; only `draft` and `approved` are recognised. */
export type FactSheetStatus = 'draft' | 'approved';
/** Fact origin on the wire (`model` = extracted, `user` = added or edited by the owner); unknown values are kept as-is. */
export type FactOrigin = 'model' | 'user' | (string & {});

export interface ResearchSource {
  readonly id: string;
  readonly kind: SourceKind;
  /** Untrusted. Render by interpolation only; link only when `isSafeHttpUrl`. */
  readonly url: string | null;
  readonly registrableDomain: string | null;
  /** Untrusted user note text. Interpolation only. */
  readonly noteText: string | null;
  readonly status: SourceStatus;
  readonly errorCode: string | null;
  readonly errorMessageKey: string | null;
  readonly httpStatus: number | null;
  /** Untrusted fetched page title. Interpolation only. */
  readonly title: string | null;
  readonly finalUrl: string | null;
  readonly contentType: string | null;
  readonly textChars: number | null;
  readonly isCited: boolean;
  readonly createdAt: string;
  readonly fetchedAt: string | null;
}

export interface SourceLimits {
  readonly maxPerEpisode: number;
  readonly urlSourcesLast24h: number;
  readonly maxUrlSourcesPerDay: number;
}

export interface SourceList {
  readonly items: readonly ResearchSource[];
  readonly limits: SourceLimits;
}

export interface FactSourceRef {
  readonly sourceId: string;
  /** Short alias such as `S1`. */
  readonly alias: string;
  /** Untrusted. Interpolation only. */
  readonly title: string;
  /** Untrusted, null for a user note. */
  readonly url: string | null;
  readonly isUserNote: boolean;
}

export interface Fact {
  /** Server id (`f1`, `f2`, ...); the empty string marks a fact added locally and not yet saved. */
  readonly id: string;
  /** Untrusted model/user text. Interpolation only. */
  readonly statement: string;
  readonly confidence: FactConfidence;
  readonly sources: readonly FactSourceRef[];
  readonly sourceIds: readonly string[];
  /** Score in [0, 1], null when unscored (02 F2). Rendered whenever non-null (task Notes [OPEN] 07). */
  readonly sourceOverlap: number | null;
  readonly origin: FactOrigin;
}

export interface FactSheet {
  readonly id: string;
  readonly version: number;
  readonly status: FactSheetStatus;
  /** `"fs-<id>"`: sent as `If-Match` on save/approve. Read from the ETag header, falling back to the body field. */
  readonly etag: string;
  readonly facts: readonly Fact[];
  readonly promptVersion: string;
  readonly createdAt: string;
  readonly approvedAt: string | null;
  readonly maxFactId: number;
}

/** One fact as sent on PUT. `id` omitted for a new fact; `confidence` omitted when the user did not change it (02.5.4-T1 T53). */
export interface FactInput {
  readonly id?: string;
  readonly statement: string;
  readonly sourceIds: readonly string[];
  readonly confidence?: FactConfidence;
}

/** Result of `POST .../fact-sheet/generate` (202). */
export interface FactSheetRun {
  readonly runId: string;
  readonly factSheetId: string | null;
  readonly status: string;
}

export const NOTE_MAX_CHARS = 3000;
export const FACT_STATEMENT_MAX_CHARS = 600;
export const SOURCE_DEFAULT_MAX_PER_EPISODE = 10;

/** True only for an absolute http(s) URL; every other scheme (javascript:, data:, vbscript:, file:, ...) is refused for `href`. */
export function isSafeHttpUrl(value: string | null | undefined): value is string {
  if (!value) return false;
  try {
    const parsed = new URL(value);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  } catch {
    return false;
  }
}
