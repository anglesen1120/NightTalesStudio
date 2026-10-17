// No Angular/rxjs/core imports in *.model.ts (README D2: domain imports only its own files and @shared/util).
// The camelCase mirror of the wire `BibleDto` (02.6.3 §3, D18/D13): no member the wire does not carry, and the
// snake_case ↔ camelCase boundary is `data-access/bible.mapper.ts`, never this file.

/**
 * Field limits of `BibleContentValidator` (02.6.3 §3.2). The three id-carrying arrays cap at 30 and the form
 * disables its add button there; `episode_summaries` is unbounded server-side (the prompt reads the newest 50), so
 * it has no add limit and no add flow — the two summary fields only bound an editable row.
 */
export const BIBLE_LIMITS = {
  narratorName: 80,
  narratorPersona: 1_000,
  narratorVoiceNotes: 1_000,
  characterName: 60,
  placeName: 60,
  description: 300,
  ruleText: 300,
  summaryTitle: 100,
  summaryBody: 200,
  maxCharacters: 30,
  maxPlaces: 30,
  maxRules: 30,
} as const;

/** One series bible as `GET /api/v1/series/{id}/bible` returns it, plus the tag the next `If-Match` must carry. */
export interface Bible {
  readonly seriesId: string;
  /** Normalised to a number: the generated wire type is `number | string`. */
  readonly version: number;
  /** The quoted `"sb-<version>"` entity tag (body member of the GET; it is also the `ETag` header). */
  readonly etag: string;
  readonly content: BibleContent;
  readonly proposal: BibleProposal | null;
}

/** The five sections of a bible; the same shape serves a load and a PUT (the PUT omits `schema_version`). */
export interface BibleContent {
  readonly narrator: BibleNarrator;
  readonly recurringCharacters: readonly BibleCharacter[];
  readonly places: readonly BiblePlace[];
  readonly rules: readonly BibleRule[];
  readonly episodeSummaries: readonly BibleEpisodeSummary[];
}

/** `narrator.{name,persona,voice_notes}`. A missing wire member reads as `''`, never `undefined` (02.6.3 §3.2). */
export interface BibleNarrator {
  readonly name: string;
  /** Required by the server once anything else exists: an empty persona makes the PUT answer 400 (STATUS BIBLE-3). */
  readonly persona: string;
  readonly voiceNotes: string;
}

/**
 * `recurring_characters[]`. `id` is absent for a locally added row and echoed for an existing one — the server
 * assigns `ch<n>` on the next save and the refetch replaces the list. `firstEpisodeId` is server-derived: shown
 * read-only and echoed back on save when the load carried one (the server owns the value either way).
 */
export interface BibleCharacter {
  readonly id?: string;
  readonly name: string;
  readonly description: string;
  readonly firstEpisodeId?: string;
}

/** `places[]`; same id rules as a character. */
export interface BiblePlace {
  readonly id?: string;
  readonly name: string;
  readonly description: string;
}

/** `rules[]`; a rule carries `text` (not a name/description pair). */
export interface BibleRule {
  readonly id?: string;
  readonly text: string;
}

/**
 * `episode_summaries[]`. Only `title` and `summary` are user-editable; `structure`/`approvedAt` are echoed from the
 * load for completeness and are omitted from a save (the server re-derives both, 02.6.3 §3.1/§4 U2).
 */
export interface BibleEpisodeSummary {
  readonly episodeId: string;
  readonly title: string;
  readonly summary: string;
  /** Server-derived structure, opaque to the web UI (never read, never sent). */
  readonly structure?: unknown;
  /** Server-derived approval timestamp of the referenced story version (echoed, never sent). */
  readonly approvedAt?: string;
}

/** A pending proposal of a story approval (02.6.3 §4 U4); `proposedFromEpisodeId` is absent for autopilot proposals. */
export interface BibleProposal {
  readonly basedOnVersion: number;
  readonly proposedFromEpisodeId: string | null;
  readonly proposedAt: string;
  readonly ops: readonly BibleOp[];
  readonly preview: BibleProposalPreview;
}

/** One op of a proposal. `kind` is kept verbatim; the banner normalises it before choosing a label (OPEN 09). */
export interface BibleOp {
  readonly kind: string;
  readonly summary?: string;
  readonly name?: string;
  readonly description?: string;
  readonly text?: string;
  readonly reason?: string;
}

/** The server-computed counts of a proposal and the episode a summary row would be added for, if any. */
export interface BibleProposalPreview {
  readonly characters: number;
  readonly places: number;
  readonly rules: number;
  readonly summaryForEpisodeId: string | null;
}
