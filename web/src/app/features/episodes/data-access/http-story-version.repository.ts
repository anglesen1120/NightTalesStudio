import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import type { AppError } from '@core/errors';
import { userAction } from '@core/http';
import { StoryEndpointsClient } from '@shared/api';
import type { StoryVersionDetail, StoryVersionSummary } from '../domain/story-version.model';
import { StoryVersionRepository } from '../domain/story-version.repository';
import { StoryMappingError } from './story.mapper';
import { mapVersionDetail, mapVersionSummary } from './story-version.mapper';

/** Version history reads. Neither route is If-Match guarded, so no ETag is read back here (task Steps 3). */
@Injectable()
export class HttpStoryVersionRepository extends StoryVersionRepository {
  private readonly client = inject(StoryEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async list(episodeId: string, opts?: { userAction?: boolean }): Promise<readonly StoryVersionSummary[]> {
    try {
      const response = await firstValueFrom(this.client.apiV1EpisodesIdStoryVersionsGet$Response(
        { id: episodeId }, opts?.userAction === true ? userAction() : undefined,
      ));
      return response.body.map((dto) => mapVersionSummary(dto));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
      return Promise.reject(this.toAppError(cause));
    }
  }

  override async detail(episodeId: string, versionId: string, opts?: { userAction?: boolean }): Promise<StoryVersionDetail> {
    try {
      const response = await firstValueFrom(this.client.apiV1EpisodesIdStoryVersionsVersionIdGet$Response(
        { id: episodeId, versionId }, opts?.userAction === true ? userAction() : undefined,
      ));
      return mapVersionDetail(response.body);
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
      return Promise.reject(this.toAppError(cause));
    }
  }

  /** A malformed success body is the client's own bug: map it to a 502 the same way `StoryHttpRepository` does. */
  private toAppError(cause: unknown): AppError {
    return cause instanceof StoryMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause);
  }
}
