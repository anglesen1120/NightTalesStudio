import type { EpisodeDetailDto, EpisodeListItemDto, RenderJobSummaryDto } from '@shared/api';
import { clampPercent } from '../application/render-stage';
import { EPISODE_STATUSES, type EpisodeDetail, type EpisodeListItem } from '../domain/episode.model';
import { RENDER_STATUSES, type RenderImages, type RenderJob, type RenderStatus, type SkippedScene } from '../domain/render.model';
import { mapStorySnapshot } from './story.mapper';

/**
 * Maps one wire row. A status this client does not know (a newer server) yields `null`: the row cannot be given a
 * status chip (`episodes.status.<status>` has no key), so the repository drops it instead of crashing the page or
 * rendering a raw wire string. `etag` and the ISO date strings pass through untouched.
 */
export function mapEpisodeListItem(dto: EpisodeListItemDto): EpisodeListItem | null {
  const status = EPISODE_STATUSES.find((known) => known === dto.status);
  if (status === undefined) return null;
  return {
    id: dto.id,
    seriesId: dto.seriesId,
    title: dto.title,
    status,
    lengthTier: dto.lengthTier,
    createdAt: dto.createdAt,
    updatedAt: dto.updatedAt,
    etag: dto.etag,
  };
}

export function mapEpisodeDetail(dto: EpisodeDetailDto): EpisodeDetail {
  const generation = dto.generation;
  const paused = (generation as { processingPaused?: unknown } | null)?.processingPaused;
  return {
    id: dto.id,
    seriesId: dto.seriesId,
    status: dto.status,
    lengthTier: dto.lengthTier,
    etag: dto.etag,
    generation: generation === null ? null : {
      kind: generation.kind,
      status: generation.status,
      stage: generation.stage,
      errorCode: generation.errorCode,
      startedAt: generation.startedAt,
      processingPaused: typeof paused === 'string' ? paused : null,
    },
    hasCurrentStory: dto.currentStory !== null,
    currentStory: dto.currentStory === null ? null : mapStorySnapshot(dto.currentStory),
    nextActions: dto.nextActions,
    renderJob: mapRenderJob(dto.renderJob),
  };
}

/**
 * The render status of the wire. Values are matched against the closed client set; a status this client does not know
 * (a newer server) becomes `cancelled` — the one status that is neither active nor an error, so an unknown job shows
 * no progress and no failure title, and does not arm the elapsed-time tick. The raw value is stored nowhere.
 */
function renderStatus(value: string): RenderStatus {
  return RENDER_STATUSES.find((known) => known === value) ?? 'cancelled';
}

/**
 * A number or numeric string as text (`42`, `4.2e1`); anything else — `''`, `'0x10'`, `Infinity`, `'1px'` — is not a
 * count the wire sent. `null` means "not usable", which the callers turn into their own default rather than a guess.
 */
const DECIMAL = /^-?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/;

/** No wire count is a paragraph: the bound keeps the test off a hostile multi-kilobyte digit string (backtracking). */
const MAX_COUNT_LENGTH = 32;

function readCount(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? Math.max(0, Math.trunc(value)) : null;
  if (typeof value !== 'string') return null;
  const text = value.trim();
  // A blank string is not "0" (`Number('')` is 0) and `Number('0x10')` is 16: neither is a value the wire sent.
  if (text.length > MAX_COUNT_LENGTH || !DECIMAL.test(text)) return null;
  const parsed = Number(text);
  return Number.isFinite(parsed) ? Math.max(0, Math.trunc(parsed)) : null;
}

/**
 * `percent` arrives as an integer or a numeric string (ng-openapi-gen `(number | string)`): it is parsed defensively
 * and clamped through the same `clampPercent` the timeline uses, so an unusable value is `0` and `250` is `100`.
 */
function readPercent(value: unknown): number {
  return clampPercent(readCount(value));
}

/** `hints` (10 CR-12) is unused by this view but part of the model: a missing, blank or non-string entry is dropped. */
function readStrings(value: unknown): readonly string[] {
  return Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === 'string' && entry !== '') : [];
}

/**
 * One skipped scene (03.4.2-T1): both members are required to name a scene the owner could fix, so a malformed entry is
 * dropped instead of being shown as a scene that failed for a reason nobody can read.
 */
function mapSkippedScene(value: unknown): SkippedScene | null {
  if (typeof value !== 'object' || value === null) return null;
  const { segment, reason } = value as { readonly segment?: unknown; readonly reason?: unknown };
  if (typeof segment !== 'string' || segment === '' || typeof reason !== 'string' || reason === '') return null;
  return { segment, reason };
}

/**
 * `renderJob.images` (03.4.2-T1, soft): not in the generated DTO yet (task Notes O-2, the mapper reads the untyped
 * side), so the shape is validated here. An object with nothing usable is `null` — the panel then omits the scene list
 * and keeps the generic body (the failure-handling table) — and every malformed scene is dropped. Missing counters of
 * a usable object are `0`, never a guess.
 */
function mapRenderImages(value: unknown): RenderImages | null {
  if (typeof value !== 'object' || value === null) return null;
  const { needed, succeeded, skipped } = value as { readonly needed?: unknown; readonly succeeded?: unknown; readonly skipped?: unknown };
  const scenes = Array.isArray(skipped)
    ? skipped.map(mapSkippedScene).filter((scene): scene is SkippedScene => scene !== null)
    : [];
  const neededCount = readCount(needed);
  const succeededCount = readCount(succeeded);
  if (neededCount === null && succeededCount === null && scenes.length === 0) return null;
  return { needed: neededCount ?? 0, succeeded: succeededCount ?? 0, skipped: scenes };
}

/**
 * The render summary as it really arrives: 03.4.2-T1 adds `images`, which the generated type does not declare yet
 * (task Notes O-2), so that one member is read as `unknown` and validated by `mapRenderImages`. Everything else is the
 * generated shape.
 */
type RenderJobWire = RenderJobSummaryDto & { readonly images?: unknown };

/**
 * The render summary (F5/F6) plus the soft fields of 10 CR-12 and 03.4.2-T1. `status` is mapped to the closed client
 * set, a null `stage` is `''` (an unknown stage is never rendered: `subStepKeyOf` fails closed), and every optional
 * field the server omits becomes `null`/`[]` instead of an error (O-2).
 */
function mapRenderJob(dto: RenderJobSummaryDto | null): RenderJob | null {
  // A missing field (older server, partial stub) arrives as `undefined` even though the generated type calls it
  // required: reading through it would throw and fail the whole detail load instead of yielding `null`.
  if (dto === null || typeof dto !== 'object') return null;
  const wire: RenderJobWire = dto;
  return {
    // Every scalar is validated as well as typed: a value of the wrong shape becomes the empty value of its field
    // rather than travelling into the model under a type it does not have (a number where an ISO string is expected
    // would reach the elapsed-time formatter).
    renderJobId: typeof dto.renderJobId === 'string' ? dto.renderJobId : '',
    status: renderStatus(dto.status),
    stage: typeof dto.stage === 'string' ? dto.stage : '',
    percent: readPercent(dto.percent),
    attempt: readCount(dto.attempt) ?? 0,
    imagesNeeded: readCount(dto.imagesNeeded) ?? 0,
    submittedAt: typeof dto.submittedAt === 'string' ? dto.submittedAt : null,
    finishedAt: typeof dto.finishedAt === 'string' ? dto.finishedAt : null,
    errorCode: typeof dto.errorCode === 'string' && dto.errorCode !== '' ? dto.errorCode : null,
    hints: readStrings(dto.hints),
    images: mapRenderImages(wire.images),
  };
}
