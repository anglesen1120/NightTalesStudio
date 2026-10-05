import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { userAction } from '@core/http';
import { EpisodeEndpointsClient } from '@shared/api';
import { CURSOR_PAGE_MAX_LIMIT, type CursorPage } from '@shared/util/cursor-page';
import type { EpisodeListItem, EpisodeStatus } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { mapEpisodeListItem } from './episode.mapper';

/**
 * HTTP adapter of the episodes port; the only file of `features/episodes` that imports the generated episodes client
 * (D5). Every list call is user-initiated, so it carries `USER_ACTION` (WR11). Filters and cursor are sent only when
 * set; `limit` is clamped to the server cap so a bad size never reaches the wire. Rows with a status this client does
 * not know are dropped by the mapper; the page envelope and `nextCursor` are kept as the server sent them.
 */
@Injectable()
export class EpisodesHttpRepository extends EpisodesRepository {
  private readonly client = inject(EpisodeEndpointsClient);

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
}
