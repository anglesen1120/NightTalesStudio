import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AppErrorMapper } from '@core/errors';
import { IdempotencyIntent, userAction, withIdempotency } from '@core/http';
import { FactSheetEndpointsClient, SourceEndpointsClient, type FactItemInputDto } from '@shared/api';
import type { FactInput, FactSheet, FactSheetRun, ResearchSource, SourceList } from '../domain/research.model';
import { ResearchRepository, type FactSheetWrite, type ResearchCommandIntent, type SourceInput } from '../domain/research.repository';
import { ResearchMappingError, mapFactSheet, mapFactSheetRun, mapFactSheetWrite, mapResearchSource, mapSourceList } from './research.mapper';

/** Keeps HTTP-context settlement linked to the domain port's caller-owned intent (10.2.3-T1). */
class ResearchCommandHttpIntent extends IdempotencyIntent {
  constructor(private readonly source: ResearchCommandIntent) {
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
 * HTTP adapter of the research port; the only file of `features/episodes` that imports the generated sources and
 * fact-sheet clients (D5). Reads carry `USER_ACTION` only when the caller started them (a click, WR11); the paid
 * generate POST is the one request that carries an `Idempotency-Key`. No response body is ever logged: sources and
 * facts are untrusted user content (10.5.5-T1 security rules).
 */
@Injectable()
export class HttpResearchRepository extends ResearchRepository {
  private readonly sources = inject(SourceEndpointsClient);
  private readonly factSheets = inject(FactSheetEndpointsClient);
  private readonly mapper = inject(AppErrorMapper);

  override async listSources(episodeId: string, opts?: { userAction?: boolean }): Promise<SourceList> {
    try {
      const response = await firstValueFrom(this.sources.apiV1EpisodesEpisodeIdSourcesGet$Response(
        { episodeId }, opts?.userAction === true ? userAction() : undefined,
      ));
      return mapSourceList(response.body);
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(cause instanceof ResearchMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }

  override async addSource(episodeId: string, input: SourceInput, intent: ResearchCommandIntent): Promise<ResearchSource> {
    try {
      const response = await firstValueFrom(this.sources.apiV1EpisodesEpisodeIdSourcesPost$Response(
        { episodeId, 'Idempotency-Key': intent.key, body: { url: input.url ?? null, noteText: input.noteText ?? null } },
        withIdempotency(new ResearchCommandHttpIntent(intent), userAction()),
      ));
      return mapResearchSource(response.body);
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(cause instanceof ResearchMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }

  override async removeSource(episodeId: string, sourceId: string): Promise<void> {
    try {
      await firstValueFrom(this.sources.apiV1EpisodesEpisodeIdSourcesSourceIdDelete({ episodeId, sourceId }, userAction()));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(this.mapper.from(cause));
    }
  }

  override async retrySource(episodeId: string, sourceId: string): Promise<ResearchSource> {
    try {
      const response = await firstValueFrom(this.sources.apiV1EpisodesEpisodeIdSourcesSourceIdRetryPost$Response(
        { episodeId, sourceId }, userAction(),
      ));
      return mapResearchSource(response.body);
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(cause instanceof ResearchMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }

  override async getFactSheet(episodeId: string, opts?: { userAction?: boolean }): Promise<FactSheet | null> {
    try {
      // The generated body type is non-nullable although the route documents a nullable document: a null payload is the
      // normal empty state and the mapper is the one place that reads it (02.5.3 `plan.md:122`).
      const response = await firstValueFrom(this.factSheets.apiV1EpisodesIdFactSheetGet$Response(
        { id: episodeId }, opts?.userAction === true ? userAction() : undefined,
      ));
      return mapFactSheet(response.body, response.headers.get('ETag'));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(cause instanceof ResearchMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }

  override async saveFactSheet(episodeId: string, etag: string, facts: readonly FactInput[]): Promise<FactSheetWrite> {
    try {
      // `id`/`confidence` are sent as null when the store omitted them: the server's nullable members read an absent and
      // a null member identically, so an existing fact keeps its server id and an untouched confidence is preserved
      // (02.5.4-T1 T53). The ETag is the concurrency token; this route takes no Idempotency-Key (a replay is a 412).
      const items: FactItemInputDto[] = facts.map((fact) => ({
        id: fact.id ?? null,
        statement: fact.statement,
        sourceIds: [...fact.sourceIds],
        confidence: fact.confidence ?? null,
      }));
      const response = await firstValueFrom(this.factSheets.apiV1EpisodesIdFactSheetPut$Response(
        { id: episodeId, 'If-Match': etag, body: { facts: items } }, userAction(),
      ));
      return mapFactSheetWrite(response.body, response.headers.get('ETag'));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(cause instanceof ResearchMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }

  override async approveFactSheet(episodeId: string, etag: string): Promise<FactSheetWrite> {
    try {
      const response = await firstValueFrom(this.factSheets.apiV1EpisodesIdFactSheetApprovePost$Response(
        { id: episodeId, 'If-Match': etag }, userAction(),
      ));
      return mapFactSheetWrite(response.body, response.headers.get('ETag'));
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(cause instanceof ResearchMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }

  override async generateFactSheet(episodeId: string, intent: ResearchCommandIntent): Promise<FactSheetRun> {
    try {
      const response = await firstValueFrom(this.factSheets.apiV1EpisodesIdFactSheetGeneratePost$Response(
        { id: episodeId, 'Idempotency-Key': intent.key },
        withIdempotency(new ResearchCommandHttpIntent(intent), userAction()),
      ));
      return mapFactSheetRun(response.body);
    } catch (cause) {
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- port rejects with the structured AppError contract
      return Promise.reject(cause instanceof ResearchMappingError ? this.mapper.fromProblem(null, 502, null) : this.mapper.from(cause));
    }
  }
}
