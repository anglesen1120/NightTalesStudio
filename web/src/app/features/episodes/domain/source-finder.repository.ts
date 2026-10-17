import type { SourceCandidate } from './source-candidate.model';

/**
 * Structural command intent of a paid/platform POST; independent of the core HTTP implementation (10.2.3-T1, D2: the
 * domain layer imports nothing of `@core`). `IdempotencyKeys.begin()`'s `IdempotencyIntent` satisfies it as it is, so
 * the caller passes its own intent straight through.
 */
export interface SourceFinderCommandIntent {
  readonly key: string;
  readonly intent: string;
  readonly isSettled: boolean;
  settle(): void;
}

/**
 * Port of the finder routes (02.7.2-T2, 02.7 `plan.md` §5). The run is a paid/platform command, so it carries the
 * caller-owned intent above: the same intent is reused by every retry of one click. Answers are the untrusted
 * candidate rows themselves; a failure is the structured `AppError` of `@core/errors`, exactly like the sibling
 * research port.
 */
export abstract class SourceFinderRepository {
  /**
   * `POST …/sources/find` (202): starts the episode's `find_sources` run. `query` is sent only when the caller has
   * one (the body member is optional); the run id and the `Location` header are not needed by the block, whose run
   * state is the episode's own `generation`.
   */
  abstract find(episodeId: string, query: string | null, intent: SourceFinderCommandIntent): Promise<void>;

  /**
   * `GET …/sources/candidates` (200): every candidate of the episode — `proposed`, `added` and `dismissed` alike — in
   * the server's own order. `opts.userAction` marks a read a click started (WR11); a realtime refetch never sets it.
   */
  abstract candidates(episodeId: string, opts?: { userAction?: boolean }): Promise<readonly SourceCandidate[]>;

  /** `POST …/candidates/{candidateId}/add` (201): the suggestion is an episode source now (re-read the list). */
  abstract add(episodeId: string, candidateId: string): Promise<void>;

  /** `POST …/candidates/{candidateId}/dismiss` (204): the suggestion is handled and stays out of the way. */
  abstract dismiss(episodeId: string, candidateId: string): Promise<void>;
}
