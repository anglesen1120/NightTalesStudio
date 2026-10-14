import type { FactInput, FactSheet, FactSheetRun, ResearchSource, SourceList } from './research.model';

/** Structural command intent of a paid/platform POST; independent of the core HTTP implementation (10.2.3-T1). */
export interface ResearchCommandIntent {
  readonly key: string;
  readonly intent: string;
  readonly isSettled: boolean;
  settle(): void;
}

/** `AddSourceRequest`: exactly one of the two members is set (02.5.1 `plan.md:181-183`). */
export interface SourceInput {
  readonly url?: string;
  readonly noteText?: string;
}

/**
 * The answer of `PUT …/fact-sheet` and `POST …/fact-sheet/approve` (`FactSheetSummaryDto`): the identity of the version
 * the write installed, not the document. A caller that needs the facts (the server assigns the new ids) re-reads the
 * sheet — `FactSheetWrite` exists so the caller can adopt the fresh `If-Match` even when that read fails.
 */
export interface FactSheetWrite {
  readonly id: string;
  readonly version: number;
  /** Forward-compatible wire value; only `draft` and `approved` are recognised. */
  readonly status: string;
  readonly etag: string;
}

/**
 * Port of the research routes (02.5.1 sources, 02.5.3/02.5.4 fact sheet). Every read carries
 * `opts.userAction` onward: only a read a click started sets it (WR11); realtime refreshes never do.
 * A failure is the structured `AppError` of `@core/errors`, exactly like the sibling episode ports.
 */
export abstract class ResearchRepository {
  abstract listSources(episodeId: string, opts?: { userAction?: boolean }): Promise<SourceList>;
  abstract addSource(episodeId: string, input: SourceInput, intent: ResearchCommandIntent): Promise<ResearchSource>;
  abstract removeSource(episodeId: string, sourceId: string): Promise<void>;
  abstract retrySource(episodeId: string, sourceId: string): Promise<ResearchSource>;
  /** `null` is the normal empty state: the route answers 200 with a null body (its only error is 403). */
  abstract getFactSheet(episodeId: string, opts?: { userAction?: boolean }): Promise<FactSheet | null>;
  abstract saveFactSheet(episodeId: string, etag: string, facts: readonly FactInput[]): Promise<FactSheetWrite>;
  abstract approveFactSheet(episodeId: string, etag: string): Promise<FactSheetWrite>;
  abstract generateFactSheet(episodeId: string, intent: ResearchCommandIntent): Promise<FactSheetRun>;
}
