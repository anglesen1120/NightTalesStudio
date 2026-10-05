import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { userAction } from '@core/http';
import { SeriesEndpointsClient } from '@shared/api';
import type { SeriesOption, SeriesOptionsPage } from '../domain/episode.model';
import { SeriesOptionsRepository } from '../domain/series-options.repository';

const FIRST_PAGE_LIMIT = 100;

/**
 * Exactly one `GET /api/v1/series?limit=100` (`includeArchived` omitted) — the page is never drained. Archived rows
 * are dropped defensively; `bounded` is true when the server still had a next cursor, i.e. the options are partial.
 */
@Injectable()
export class SeriesOptionsHttpRepository extends SeriesOptionsRepository {
  private readonly client = inject(SeriesEndpointsClient);

  override async list(): Promise<SeriesOptionsPage> {
    const page = await firstValueFrom(this.client.apiV1SeriesGet({ limit: FIRST_PAGE_LIMIT }, userAction()));
    const items: SeriesOption[] = page.items
      .filter((series) => series.archivedAt === null)
      .map((series) => ({ id: series.id, title: series.title }));
    return { items, bounded: page.nextCursor !== null && page.nextCursor !== '' };
  }
}
