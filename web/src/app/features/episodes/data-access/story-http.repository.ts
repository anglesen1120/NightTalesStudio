import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { userAction } from '@core/http';
import { StoryEndpointsClient } from '@shared/api';
import type { StoryDraftWire, StorySnapshot } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';
import { StoryMappingError, mapStorySnapshot } from './story.mapper';

@Injectable()
export class StoryHttpRepository extends StoryRepository {
  private readonly client = inject(StoryEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async save(episodeId: string, etag: string, content: StoryDraftWire, opts: { userAction: true }): Promise<StorySnapshot> {
    try {
      // StoryEndpoints.EditAsync deserializes the raw story.v1 object, NOT a { content } envelope.
      const response = await firstValueFrom(this.client.apiV1EpisodesIdStoryPut$Response(
        { id: episodeId, 'If-Match': etag, body: content }, opts.userAction ? userAction() : undefined,
      ));
      return mapStorySnapshot(response.body, response.headers.get('ETag'));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
      return Promise.reject(cause instanceof StoryMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }
}
