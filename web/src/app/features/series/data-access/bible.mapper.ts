import type {
  BibleCharacterV1,
  BibleContentResponseV1,
  BibleDto,
  BibleEpisodeSummaryResponseV1,
  BibleEpisodeSummaryV1,
  BibleNarratorV1,
  BibleOpDto,
  BiblePlaceV1,
  BibleProposalDto,
  BibleRuleV1,
  BibleUpdateResponse,
} from '@shared/api';
import type { BibleWriteResult } from '../application/bible.repository';
import type {
  Bible,
  BibleCharacter,
  BibleContent,
  BibleEpisodeSummary,
  BibleNarrator,
  BibleOp,
  BiblePlace,
  BibleProposal,
  BibleProposalPreview,
  BibleRule,
} from '../domain/bible.model';
import { isEntityTag } from './catalog.http-repository';

/**
 * The `PUT { content }` shape: the snake_case `bible.v1` **without** `schema_version` (the backend writes it and
 * ignores a client value, 02.6.3 §3.1). An `id` is absent — not `null` — for a locally added entry: the server
 * assigns `ch|pl|rl<n>` and the refetch replaces the list (owner decision 2026-10-15). Summaries carry only the
 * three writable members; `structure`/`approved_at` are re-derived server-side and are never sent.
 */
export interface BibleContentWire {
  narrator: BibleNarratorV1;
  recurring_characters: Array<Omit<BibleCharacterV1, 'id'> & { id?: string }>;
  places: Array<Omit<BiblePlaceV1, 'id'> & { id?: string }>;
  rules: Array<Omit<BibleRuleV1, 'id'> & { id?: string }>;
  episode_summaries: BibleEpisodeSummaryV1[];
}

/** A wire string member, defaulting to `''`: `narrator.persona` is required and a missing one must never be `undefined`. */
function text(value: string | null | undefined): string {
  return typeof value === 'string' ? value : '';
}

/** An optional wire string: `null` and `''` both mean "the server did not send it", so neither is echoed back. */
function optionalText(value: string | null | undefined): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

/** ng-openapi-gen types every counter as `number | string`; the model always holds a finite number. */
function numberFromWire(value: number | string | null | undefined): number {
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/** `narrator.{name,persona,voice_notes}` → `{name,persona,voiceNotes}`; a missing persona becomes `''`. */
function narratorFromWire(wire: BibleNarratorV1 | null | undefined): BibleNarrator {
  return { name: text(wire?.name), persona: text(wire?.persona), voiceNotes: text(wire?.voice_notes) };
}

/** `narrator` → `narrator.{name,persona,voice_notes}`. */
function narratorToWire(narrator: BibleNarrator): BibleNarratorV1 {
  return { name: narrator.name, persona: narrator.persona, voice_notes: narrator.voiceNotes };
}

/** One `recurring_characters[]` entry, every field explicit. */
function characterFromWire(wire: BibleCharacterV1): BibleCharacter {
  return {
    id: optionalText(wire.id),
    name: text(wire.name),
    description: text(wire.description),
    firstEpisodeId: optionalText(wire.first_episode_id),
  };
}

/** One character on the wire; a new row (no `id`) omits the member instead of sending `null`. */
function characterToWire(character: BibleCharacter): BibleContentWire['recurring_characters'][number] {
  return {
    ...(character.id === undefined ? {} : { id: character.id }),
    name: character.name,
    description: character.description,
    first_episode_id: character.firstEpisodeId ?? null,
  };
}

/** One `places[]` entry. */
function placeFromWire(wire: BiblePlaceV1): BiblePlace {
  return { id: optionalText(wire.id), name: text(wire.name), description: text(wire.description) };
}

/** One place on the wire; a new row omits `id`. */
function placeToWire(place: BiblePlace): BibleContentWire['places'][number] {
  return {
    ...(place.id === undefined ? {} : { id: place.id }),
    name: place.name,
    description: place.description,
  };
}

/** One `rules[]` entry: a rule carries `text`, never a name/description pair. */
function ruleFromWire(wire: BibleRuleV1): BibleRule {
  return { id: optionalText(wire.id), text: text(wire.text) };
}

/** One rule on the wire; a new row omits `id`. */
function ruleToWire(rule: BibleRule): BibleContentWire['rules'][number] {
  return { ...(rule.id === undefined ? {} : { id: rule.id }), text: rule.text };
}

/** One `episode_summaries[]` entry as the load answers it: `structure`/`approvedAt` are echoed, not interpreted. */
function summaryFromWire(wire: BibleEpisodeSummaryResponseV1): BibleEpisodeSummary {
  return {
    episodeId: text(wire.episode_id),
    title: text(wire.title),
    summary: text(wire.summary),
    ...(wire.structure === null || wire.structure === undefined ? {} : { structure: wire.structure }),
    ...(wire.approved_at === null || wire.approved_at === undefined ? {} : { approvedAt: wire.approved_at }),
  };
}

/** One summary on the wire: only the three writable members (`structure`/`approved_at` are server-derived). */
function summaryToWire(summary: BibleEpisodeSummary): BibleEpisodeSummaryV1 {
  return { episode_id: summary.episodeId, title: summary.title, summary: summary.summary };
}

/** One `ops[]` entry; every optional member is copied only when the wire carried a string. */
function opFromWire(wire: BibleOpDto): BibleOp {
  return {
    kind: text(wire.kind),
    ...(typeof wire.summary === 'string' ? { summary: wire.summary } : {}),
    ...(typeof wire.name === 'string' ? { name: wire.name } : {}),
    ...(typeof wire.description === 'string' ? { description: wire.description } : {}),
    ...(typeof wire.text === 'string' ? { text: wire.text } : {}),
    ...(typeof wire.reason === 'string' ? { reason: wire.reason } : {}),
  };
}

/** The server-computed preview counts of a proposal. */
function previewFromWire(wire: BibleProposalDto['preview']): BibleProposalPreview {
  return {
    characters: numberFromWire(wire.characters),
    places: numberFromWire(wire.places),
    rules: numberFromWire(wire.rules),
    summaryForEpisodeId: optionalText(wire.summaryForEpisodeId) ?? null,
  };
}

/** A pending proposal, or `null`/absent (no proposal); `proposedFromEpisodeId` is null for an autopilot proposal. */
function proposalFromWire(wire: BibleProposalDto | null | undefined): BibleProposal | null {
  if (wire === null || wire === undefined) return null;
  return {
    basedOnVersion: numberFromWire(wire.basedOnVersion),
    proposedFromEpisodeId: optionalText(wire.proposedFromEpisodeId) ?? null,
    proposedAt: text(wire.proposedAt),
    ops: (wire.ops ?? []).map(opFromWire),
    preview: previewFromWire(wire.preview),
  };
}

/** The five sections of a load, each mapped field by field; `schema_version` has no model member and is dropped. */
export function mapBibleContent(wire: BibleContentResponseV1): BibleContent {
  return {
    narrator: narratorFromWire(wire.narrator),
    recurringCharacters: (wire.recurring_characters ?? []).map(characterFromWire),
    places: (wire.places ?? []).map(placeFromWire),
    rules: (wire.rules ?? []).map(ruleFromWire),
    episodeSummaries: (wire.episode_summaries ?? []).map(summaryFromWire),
  };
}

/**
 * The `PUT { content }` body, snake_case and explicit: unknown model members cannot leak onto the wire, and
 * `schema_version` is never written. `structure`/`approved_at` are omitted from every summary.
 */
export function mapBibleContentToWire(content: BibleContent): BibleContentWire {
  return {
    narrator: narratorToWire(content.narrator),
    recurring_characters: content.recurringCharacters.map(characterToWire),
    places: content.places.map(placeToWire),
    rules: content.rules.map(ruleToWire),
    episode_summaries: content.episodeSummaries.map(summaryToWire),
  };
}

/**
 * `GET /series/{id}/bible` → model. The body `etag` is the contract (the same quoted value the `ETag` header
 * carries); the header is only a fallback when the body carries none. `version` is normalised to a number.
 */
export function mapBible(dto: BibleDto, headerEtag: string | null = null): Bible {
  const bodyEtag = typeof dto.etag === 'string' && isEntityTag(dto.etag) ? dto.etag : null;
  return {
    seriesId: dto.seriesId,
    version: numberFromWire(dto.version),
    etag: bodyEtag ?? headerEtag ?? '',
    content: mapBibleContent(dto.content),
    proposal: proposalFromWire(dto.proposal),
  };
}

/**
 * `PUT`/`accept` 200 body → `{ version, etag }`. The `ETag` header wins over the body member (both are the same
 * value, 02.6.3 §5); an unusable pair yields `''`, which the store replaces with the refetched GET's tag.
 */
export function mapBibleWrite(response: BibleUpdateResponse, headerEtag: string | null): BibleWriteResult {
  return {
    version: numberFromWire(response.version),
    etag: headerEtag ?? (typeof response.etag === 'string' && isEntityTag(response.etag) ? response.etag : ''),
  };
}
