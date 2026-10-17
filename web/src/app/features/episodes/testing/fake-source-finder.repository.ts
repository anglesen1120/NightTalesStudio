import type { SourceCandidate } from '../domain/source-candidate.model';
import { SourceFinderRepository, type SourceFinderCommandIntent } from '../domain/source-finder.repository';

/** One scripted answer per call; a call with no script left answers the seeded state. */
type Answer<T> = () => Promise<T>;

/**
 * Hand-written scriptable `SourceFinderRepository` (README D15: no mocking library). Each call records its arguments
 * — including the `Idempotency-Key`'s intent label and key a `find` was sent with, so a spec can prove which intent a
 * retry replayed — and answers the next scripted reply, falling back to the seeded state.
 */
export class FakeSourceFinderRepository extends SourceFinderRepository {
  readonly findCalls: Array<{ episodeId: string; query: string | null; key: string; intent: string }> = [];
  readonly candidatesCalls: Array<{ episodeId: string; userAction: boolean }> = [];
  readonly addCalls: Array<{ episodeId: string; candidateId: string }> = [];
  readonly dismissCalls: Array<{ episodeId: string; candidateId: string }> = [];

  /** Rows `candidates` answers with once its script is empty. */
  candidatesResult: readonly SourceCandidate[] = [];

  private readonly findScript: Array<Answer<void>> = [];
  private readonly candidatesScript: Array<Answer<readonly SourceCandidate[]>> = [];
  private readonly addScript: Array<Answer<void>> = [];
  private readonly dismissScript: Array<Answer<void>> = [];

  seed(rows: readonly SourceCandidate[]): this {
    this.candidatesResult = rows;
    return this;
  }

  scriptFind(answer: Answer<void>): this { this.findScript.push(answer); return this; }
  scriptCandidates(answer: Answer<readonly SourceCandidate[]>): this { this.candidatesScript.push(answer); return this; }
  scriptAdd(answer: Answer<void>): this { this.addScript.push(answer); return this; }
  scriptDismiss(answer: Answer<void>): this { this.dismissScript.push(answer); return this; }

  override find(episodeId: string, query: string | null, intent: SourceFinderCommandIntent): Promise<void> {
    this.findCalls.push({ episodeId, query, key: intent.key, intent: intent.intent });
    return this.findScript.shift()?.() ?? Promise.resolve();
  }

  override candidates(episodeId: string, opts?: { userAction?: boolean }): Promise<readonly SourceCandidate[]> {
    this.candidatesCalls.push({ episodeId, userAction: opts?.userAction === true });
    return this.candidatesScript.shift()?.() ?? Promise.resolve(this.candidatesResult);
  }

  override add(episodeId: string, candidateId: string): Promise<void> {
    this.addCalls.push({ episodeId, candidateId });
    return this.addScript.shift()?.() ?? Promise.resolve();
  }

  override dismiss(episodeId: string, candidateId: string): Promise<void> {
    this.dismissCalls.push({ episodeId, candidateId });
    return this.dismissScript.shift()?.() ?? Promise.resolve();
  }
}

/** A candidate row with defaults; `n` makes the id/url/site/rank unique and ordered. */
export function candidate(n: number, overrides: Partial<SourceCandidate> = {}): SourceCandidate {
  return {
    id: `candidate-${String(n)}`,
    url: `https://example${String(n)}.com/article`,
    title: `Suggestion ${String(n)}`,
    siteName: `example${String(n)}.com`,
    rank: n,
    status: 'proposed',
    sourceId: null,
    createdAt: '2026-10-01T10:00:00Z',
    ...overrides,
  };
}
