import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { IdempotencyIntent, userAction, withIdempotency } from '@core/http';
import { EpisodeEndpointsClient, StoryEndpointsClient } from '@shared/api';
import { CURSOR_PAGE_MAX_LIMIT, type CursorPage } from '@shared/util/cursor-page';
import { isUuid } from '../domain/episode.model';
import type { CreatedEpisode, EpisodeDetail, EpisodeListItem, EpisodeStatus, LengthTier } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import type { StoryGenerationIntent } from '../domain/episodes.repository';
import { mapEpisodeDetail, mapEpisodeListItem } from './episode.mapper';

/**
 * HTTP adapter of the episodes port; the only file of `features/episodes` that imports the generated episodes client
 * (D5). Every list call is user-initiated, so it carries `USER_ACTION` (WR11). Filters and cursor are sent only when
 * set; `limit` is clamped to the server cap so a bad size never reaches the wire. Rows with a status this client does
 * not know are dropped by the mapper; the page envelope and `nextCursor` are kept as the server sent them.
 */
@Injectable()
export class EpisodesHttpRepository extends EpisodesRepository {
  private readonly client = inject(EpisodeEndpointsClient);
  private readonly stories = inject(StoryEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async list(q: {
    status?: EpisodeStatus;
    seriesId?: string;
    cursor?: string;
    limit: number;
  }): Promise<CursorPage<EpisodeListItem>> {
    const page = await firstValueFrom(
      this.client.apiV1EpisodesGet(
        {
          ...(q.status === undefined ? {} : { status: q.status }),
          ...(q.seriesId === undefined ? {} : { seriesId: q.seriesId }),
          ...(q.cursor === undefined || q.cursor === '' ? {} : { cursor: q.cursor }),
          limit: Math.min(q.limit, CURSOR_PAGE_MAX_LIMIT),
        },
        userAction(),
      ),
    );
    const items: EpisodeListItem[] = [];
    for (const dto of page.items) {
      const item = mapEpisodeListItem(dto);
      if (item !== null) items.push(item);
    }
    return { items, nextCursor: page.nextCursor };
  }

  override async create(seriesId: string, input: { title: string | null; brief: string; lengthTier: LengthTier }): Promise<CreatedEpisode> {
    try {
      const response = await firstValueFrom(this.client.apiV1SeriesSeriesIdEpisodesPost$Response({ seriesId, body: input }, userAction()));
      // HttpClient has already decoded the JSON scalar. Object envelopes are not this endpoint's contract.
      if (!isUuid(response.body)) throw new Error('Invalid episode create response');
      return { id: response.body, etag: response.headers.get('ETag') };
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }

  override async generateStory(episodeId: string, intent: StoryGenerationIntent): Promise<void> {
    try {
      const contextIntent = intent instanceof IdempotencyIntent ? intent : new IdempotencyIntent(intent.key, intent.intent);
      await firstValueFrom(this.stories.apiV1EpisodesIdStoryGeneratePost(
        { id: episodeId, 'Idempotency-Key': intent.key },
        withIdempotency(contextIntent, userAction()),
      ));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }

  override async get(episodeId: string, opts?: { userAction?: boolean }): Promise<EpisodeDetail> {
    try {
      // Generated params expose no If-None-Match; keep the generated adapter rather than hand-building a GET.
      const dto = await firstValueFrom(this.client.apiV1EpisodesIdGet({ id: episodeId }, opts?.userAction === true ? userAction() : undefined));
      return mapEpisodeDetail(dto);
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }
}
