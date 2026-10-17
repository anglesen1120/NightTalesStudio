import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { IdempotencyIntent, userAction, withIdempotency } from '@core/http';
import { SourceFinderEndpointsClient } from '@shared/api';
import type { SourceCandidate } from '../domain/source-candidate.model';
import { SourceFinderRepository, type SourceFinderCommandIntent } from '../domain/source-finder.repository';
import { SourceCandidateMappingError, mapSourceCandidateList } from './source-candidate.mapper';

/** Keeps HTTP-context settlement linked to the domain port's caller-owned intent (10.2.3-T1). */
class SourceFinderCommandHttpIntent extends IdempotencyIntent {
  constructor(private readonly source: SourceFinderCommandIntent) {
    super(source.key, source.intent);
  }

  override get isSettled(): boolean {
    return super.isSettled || this.source.isSettled;
  }

  override settle(): void {
    super.settle();
    this.source.settle();
  }
}

/**
 * HTTP adapter of the finder port; the only file of `features/episodes` that imports the generated finder client (D5).
 * The run carries an `Idempotency-Key` (one key per click, replayed by the intent's owner); every failure becomes the
 * shared `AppError`. No response body is ever logged: candidate titles, site names and URLs are untrusted third-party
 * text (10.5.8-T1 security rules).
 */
@Injectable()
export class HttpSourceFinderRepository extends SourceFinderRepository {
  private readonly finder = inject(SourceFinderEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async find(episodeId: string, query: string | null, intent: SourceFinderCommandIntent): Promise<void> {
    try {
      // 202: from here the run is the episode's own state (`generation.kind = find_sources`), which the page reads
      // with the detail. The accepted body (`generationRunId`) and the `Location` header add nothing to the block.
      await firstValueFrom(this.finder.apiV1EpisodesEpisodeIdSourcesFindPost$Response(
        { episodeId, 'Idempotency-Key': intent.key, body: { query } },
        withIdempotency(new SourceFinderCommandHttpIntent(intent), userAction()),
      ));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }

  override async candidates(episodeId: string, opts?: { userAction?: boolean }): Promise<readonly SourceCandidate[]> {
    try {
      const response = await firstValueFrom(this.finder.apiV1EpisodesEpisodeIdSourcesCandidatesGet$Response(
        { episodeId }, opts?.userAction === true ? userAction() : undefined,
      ));
      return mapSourceCandidateList(response.body);
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(cause instanceof SourceCandidateMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }

  override async add(episodeId: string, candidateId: string): Promise<void> {
    try {
      // 201: the suggestion is an episode source now. The answered row is not adopted here — the page re-reads both
      // lists, so the source list, its counters and the candidate statuses stay the server's own.
      await firstValueFrom(this.finder.apiV1EpisodesEpisodeIdSourcesCandidatesCandidateIdAddPost$Response(
        { episodeId, candidateId }, userAction(),
      ));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }

  override async dismiss(episodeId: string, candidateId: string): Promise<void> {
    try {
      // 204: no body to read; the row's new status is the local answer of the store.
      await firstValueFrom(this.finder.apiV1EpisodesEpisodeIdSourcesCandidatesCandidateIdDismissPost$Response(
        { episodeId, candidateId }, userAction(),
      ));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }
}
