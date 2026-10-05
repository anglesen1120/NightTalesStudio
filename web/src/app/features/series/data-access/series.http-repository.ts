import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type Result } from '@core/errors';
import {
  SeriesEndpointsClient,
  type CreateSeriesCommand,
  type SeriesDto,
  type SeriesListItemDto,
  type UpdateSeriesRequest,
} from '@shared/api';
import {
  SeriesRepository,
  type CreatedSeries,
  type SeriesListQuery,
  type SeriesPage,
  type UpdatedSeries,
} from '../application/series.repository';
import { isAspect, isTopic, type CreateSeriesInput, type Series, type SeriesListItem, type SeriesPatchInput } from '../domain/series.model';
import { isLanguage } from '../domain/voice.model';
import { isEntityTag, readEtag } from './catalog.http-repository';

/** The members a list row and a full series share; both DTOs satisfy it, so one mapper serves both. */
type SeriesRowDto = Pick<
  SeriesListItemDto,
  | 'id'
  | 'title'
  | 'topic'
  | 'language'
  | 'aspect'
  | 'voiceId'
  | 'styleTemplateId'
  | 'autopilotEnabled'
  | 'archivedAt'
  | 'createdAt'
  | 'updatedAt'
>;

/**
 * DTO -> model, explicit field by field. A row whose `topic`, `language` or `aspect` is outside the closed wire
 * vocabulary is dropped by the caller (`null`), never rendered with a raw value: `storyPreset` is not part of a list
 * item at all (`SeriesListItemDto` does not carry it, and neither does the model).
 */
export function toSeriesListItem(dto: SeriesRowDto): SeriesListItem | null {
  if (!isTopic(dto.topic) || !isLanguage(dto.language) || !isAspect(dto.aspect)) return null;
  return {
    id: dto.id,
    title: dto.title,
    topic: dto.topic,
    language: dto.language,
    aspect: dto.aspect,
    voiceId: dto.voiceId,
    styleTemplateId: dto.styleTemplateId,
    autopilotEnabled: dto.autopilotEnabled === true,
    archivedAt: dto.archivedAt ?? null,
    createdAt: dto.createdAt,
    updatedAt: dto.updatedAt,
  };
}

/**
 * DTO -> model for one series. `storyPreset` keeps the absent/explicit-null distinction meaningless here (the GET is
 * not a merge patch): anything that is not a string is `null`, which is what "no preset" means. The `etag` comes from
 * the body — the same value the `ETag` header carries (09.4.1 §5) — and is validated as an entity tag before use.
 */
export function toSeries(dto: SeriesDto): Series | null {
  const listed = toSeriesListItem(dto);
  if (listed === null) return null;
  return {
    ...listed,
    storyPreset: typeof dto.storyPreset === 'string' ? dto.storyPreset : null,
    etag: isEntityTag(dto.etag) ? dto.etag : '',
  };
}

/**
 * The `PATCH` body: only the members the caller marked as changed are present, so the server keeps everything else
 * (09.4.1 §5 JSON-merge patch). `language` is never sent (immutable, 09.4.1 §3) and an absent `storyPreset` means
 * "keep the stored preset"; an explicit `null` clears it.
 *
 * The generated `UpdateSeriesRequest` types every member as required, while the wire contract needs them absent —
 * hence the cast. The object is built dynamically on purpose: writing `null` for an unchanged member would be a
 * different (though for these members equivalent) request, and the acceptance is "only changed fields".
 */
function toPatchBody(patch: SeriesPatchInput): UpdateSeriesRequest {
  const body: Record<string, unknown> = {};
  if (patch.title !== undefined) body['title'] = patch.title;
  if (patch.topic !== undefined) body['topic'] = patch.topic;
  if (patch.aspect !== undefined) body['aspect'] = patch.aspect;
  if (patch.voiceId !== undefined) body['voiceId'] = patch.voiceId;
  if (patch.styleTemplateId !== undefined) body['styleTemplateId'] = patch.styleTemplateId;
  if (patch.storyPreset !== undefined) body['storyPreset'] = patch.storyPreset;
  return body as unknown as UpdateSeriesRequest;
}

/**
 * The series write/read adapter: the only `features/series` file besides the catalog adapter that imports the
 * generated client (D5). One request per call, failures returned as `{ ok: false }` through `AppErrorMapper`.
 *
 * `patch()` reads the committed ETag header first and the `{ etag }` body second (both are the same value,
 * 09.4.1 §5) and never fabricates a `Series`: a 200 body holds nothing but the tag. `nextCursor` is opaque — it is
 * passed through exactly as the wire sent it, never parsed or rebuilt.
 */
@Injectable()
export class SeriesHttpRepository extends SeriesRepository {
  private readonly client = inject(SeriesEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async list(query: SeriesListQuery): Promise<Result<SeriesPage>> {
    try {
      const response = await firstValueFrom(
        this.client.apiV1SeriesGet$Response({
          cursor: query.cursor ?? undefined,
          limit: query.limit,
          includeArchived: query.includeArchived,
        }),
      );
      const items = response.body.items.map(toSeriesListItem).filter((item): item is SeriesListItem => item !== null);
      const nextCursor = typeof response.body.nextCursor === 'string' ? response.body.nextCursor : null;
      return { ok: true, value: { items, nextCursor } };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async get(id: string): Promise<Result<Series>> {
    try {
      const response = await firstValueFrom(this.client.apiV1SeriesIdGet$Response({ id }));
      const series = toSeries(response.body);
      if (series === null) throw new Error('GET /api/v1/series/{id} returned a series outside the wire vocabulary');
      return { ok: true, value: series };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async create(input: CreateSeriesInput): Promise<Result<CreatedSeries>> {
    try {
      const body: CreateSeriesCommand = {
        title: input.title,
        topic: input.topic,
        language: input.language,
        aspect: input.aspect,
        voiceId: input.voiceId,
        styleTemplateId: input.styleTemplateId,
        storyPreset: input.storyPreset,
      };
      const response = await firstValueFrom(this.client.apiV1SeriesPost$Response({ body }));
      return { ok: true, value: { id: response.body.id, etag: readEtag(response.headers) } };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async patch(id: string, patch: SeriesPatchInput, etag: string): Promise<Result<UpdatedSeries>> {
    try {
      const response = await firstValueFrom(
        this.client.apiV1SeriesIdPatch$Response({ id, 'If-Match': etag, body: toPatchBody(patch) }),
      );
      const header = readEtag(response.headers);
      const carried = response.body.etag;
      // The `ETag` header wins; the body member is the documented fallback for a client that only reads bodies.
      const committed = header ?? (isEntityTag(carried) ? carried : null);
      if (committed === null) throw new Error('PATCH /api/v1/series/{id} answered 200 without a usable ETag');
      return { ok: true, value: { etag: committed } };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async archive(id: string): Promise<Result<void>> {
    try {
      await firstValueFrom(this.client.apiV1SeriesIdDelete$Response({ id }));
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }
}
