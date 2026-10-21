import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { userAction } from '@core/http';
import { StoryEndpointsClient, type ApprovedStoryDto, type EpisodeStoryStateDto } from '@shared/api';
import type { ApprovalOutcome, AttestationAcceptance, StoryDraftWire, StorySnapshot } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';
import { StoryMappingError, mapStorySnapshot } from './story.mapper';

/**
 * One approve/reopen answer → the episode's new story state. The `ETag` response header wins, the body `etag` is the
 * fallback — the same precedence `mapStorySnapshot` applies, because both describe the state the response just
 * installed. A body without a usable `status`/ETag is a mapping failure (502), never a fabricated state: the caller
 * records what it returns as the current status and reuses the ETag as the next `If-Match`. The wire's
 * `storyVersionId` is not read: no consumer needs it, so its absence must not fail a committed approve.
 */
export function mapApprovalOutcome(body: ApprovedStoryDto | EpisodeStoryStateDto, responseEtag?: string | null): ApprovalOutcome {
  const status: unknown = body?.status;
  const bodyEtag: unknown = body?.etag;
  const etag = responseEtag ?? bodyEtag;
  if (typeof status !== 'string' || status === '' || typeof etag !== 'string' || etag === '') throw new StoryMappingError();
  return { status, etag };
}

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

  override async approve(episodeId: string, etag: string, attestation?: AttestationAcceptance): Promise<ApprovalOutcome> {
    try {
      // The field is omitted, not nulled, when the user was shown no block: the server must not read a tick nobody gave.
      const response = await firstValueFrom(this.client.apiV1EpisodesIdStoryApprovePost$Response(
        attestation === undefined
          ? { id: episodeId, 'If-Match': etag }
          : { id: episodeId, 'If-Match': etag, body: { attestation: { accepted: attestation.accepted, textVersion: attestation.textVersion } } },
        userAction(),
      ));
      return mapApprovalOutcome(response.body, response.headers.get('ETag'));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
      return Promise.reject(cause instanceof StoryMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }

  override async reopen(episodeId: string, etag: string): Promise<ApprovalOutcome> {
    try {
      const response = await firstValueFrom(this.client.apiV1EpisodesIdStoryReopenPost$Response({ id: episodeId, 'If-Match': etag }, userAction()));
      return mapApprovalOutcome(response.body, response.headers.get('ETag'));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- the port's structured AppError contract
      return Promise.reject(cause instanceof StoryMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }
}
