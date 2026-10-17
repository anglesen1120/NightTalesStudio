import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper, type Result } from '@core/errors';
import { EpisodeEndpointsClient, SeriesBibleClient, type UpdateBibleRequest } from '@shared/api';
import { BibleRepository, type BibleWriteResult } from '../application/bible.repository';
import type { Bible, BibleContent } from '../domain/bible.model';
import { mapBible, mapBibleContentToWire, mapBibleWrite } from './bible.mapper';
import { readEtag } from './catalog.http-repository';

/**
 * The series-bible read/write adapter: one of the two `features/series` files that import the generated client (D5).
 * One request per call; every failure becomes the structured `AppError` through `AppErrorMapper` (D9). No bible text
 * is ever logged: it is user- and model-authored content (10.5.7 security rules).
 *
 * `save()` is the only call that carries `If-Match` (the exact quoted `"sb-<version>"` the caller loaded) and a
 * body; `accept()`/`dismiss()` send neither (02.6.3 §4 U3/U4) and are therefore safe to repeat. The proposal banner
 * title costs one `GET /episodes/{id}` whose failure is deliberately silent (`null`), never an error state (P5).
 */
@Injectable()
export class BibleHttpRepository extends BibleRepository {
  private readonly client = inject(SeriesBibleClient);
  private readonly episodes = inject(EpisodeEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async get(seriesId: string): Promise<Result<Bible>> {
    try {
      const response = await firstValueFrom(this.client.apiV1SeriesIdBibleGet$Response({ id: seriesId }));
      return { ok: true, value: mapBible(response.body, readEtag(response.headers)) };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async save(seriesId: string, content: BibleContent, etag: string): Promise<Result<BibleWriteResult>> {
    try {
      // `BibleContentWire` is the request contract: snake_case, no `schema_version`, ids omitted for new entries. The
      // generated `UpdateBibleRequest` types `schema_version` as required-nullable, so the cast is the one place that
      // states the wire intentionally has no such member (02.6.3 §3.1: the backend writes it and ignores a client value).
      const body = { content: mapBibleContentToWire(content) } as unknown as UpdateBibleRequest;
      const response = await firstValueFrom(this.client.apiV1SeriesIdBiblePut$Response({
        id: seriesId,
        'If-Match': etag,
        body,
      }));
      return { ok: true, value: mapBibleWrite(response.body, readEtag(response.headers)) };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async accept(seriesId: string): Promise<Result<void>> {
    try {
      await firstValueFrom(this.client.apiV1SeriesIdBibleProposalAcceptPost({ id: seriesId }));
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async dismiss(seriesId: string): Promise<Result<void>> {
    try {
      await firstValueFrom(this.client.apiV1SeriesIdBibleProposalDismissPost({ id: seriesId }));
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.mapper.from(cause) };
    }
  }

  override async getEpisodeTitle(episodeId: string): Promise<string | null> {
    try {
      const episode = await firstValueFrom(this.episodes.apiV1EpisodesIdGet({ id: episodeId }));
      // Only the title is read; every other member of the detail payload stays out of the bible feature.
      return typeof episode.title === 'string' ? episode.title : null;
    } catch {
      // 404/403/network: the banner falls back to the no-title banner copy — no error, no retry (P5).
      return null;
    }
  }
}
