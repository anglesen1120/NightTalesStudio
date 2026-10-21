import type { Fact, FactInput, FactSheet, FactSheetRun, ResearchSource, SourceKind, SourceLimits, SourceList } from '../domain/research.model';
import { ResearchRepository, type FactSheetWrite, type ResearchCommandIntent, type SourceInput } from '../domain/research.repository';

export const RESEARCH_EPISODE_ID = '22222222-2222-4222-8222-222222222222';

/** One scripted answer per call; a call with no script left answers the seeded state. */
type Answer<T> = () => Promise<T>;

/**
 * Hand-written scriptable `ResearchRepository` (README D15: no mocking library). Each call records its arguments and
 * answers the next scripted reply, falling back to the seeded state — so a spec scripts only the answers it cares about
 * and asserts the wire shape of the rest.
 */
export class FakeResearchRepository extends ResearchRepository {
  readonly listCalls: Array<{ episodeId: string; userAction: boolean }> = [];
  readonly addCalls: Array<{ episodeId: string; input: SourceInput; key: string; intent: string }> = [];
  readonly removeCalls: Array<{ episodeId: string; sourceId: string }> = [];
  readonly retryCalls: Array<{ episodeId: string; sourceId: string }> = [];
  readonly sheetCalls: Array<{ episodeId: string; userAction: boolean }> = [];
  readonly saveCalls: Array<{ episodeId: string; etag: string; facts: readonly FactInput[] }> = [];
  readonly approveCalls: Array<{ episodeId: string; etag: string }> = [];
  readonly generateCalls: Array<{ episodeId: string; key: string; intent: string }> = [];

  listResult: SourceList = { items: [], limits: sourceLimits() };
  sheetResult: FactSheet | null = null;
  addResult: ResearchSource = source(1);
  retryResult: ResearchSource = source(2, { status: 'pending', errorCode: null });
  writeResult: FactSheetWrite = { id: 'sheet-1', version: 2, status: 'draft', etag: '"fs-2"' };
  generateResult: FactSheetRun = { runId: 'run-1', factSheetId: null, status: 'queued' };

  private readonly listScript: Array<Answer<SourceList>> = [];
  private readonly addScript: Array<Answer<ResearchSource>> = [];
  private readonly removeScript: Array<Answer<void>> = [];
  private readonly retryScript: Array<Answer<ResearchSource>> = [];
  private readonly sheetScript: Array<Answer<FactSheet | null>> = [];
  private readonly saveScript: Array<Answer<FactSheetWrite>> = [];
  private readonly approveScript: Array<Answer<FactSheetWrite>> = [];
  private readonly generateScript: Array<Answer<FactSheetRun>> = [];

  seed(list: SourceList, sheet: FactSheet | null = null): this {
    this.listResult = list;
    this.sheetResult = sheet;
    return this;
  }

  scriptList(answer: Answer<SourceList>): this { this.listScript.push(answer); return this; }
  scriptAdd(answer: Answer<ResearchSource>): this { this.addScript.push(answer); return this; }
  scriptRemove(answer: Answer<void>): this { this.removeScript.push(answer); return this; }
  scriptRetry(answer: Answer<ResearchSource>): this { this.retryScript.push(answer); return this; }
  scriptSheet(answer: Answer<FactSheet | null>): this { this.sheetScript.push(answer); return this; }
  scriptSave(answer: Answer<FactSheetWrite>): this { this.saveScript.push(answer); return this; }
  scriptApprove(answer: Answer<FactSheetWrite>): this { this.approveScript.push(answer); return this; }
  scriptGenerate(answer: Answer<FactSheetRun>): this { this.generateScript.push(answer); return this; }

  override listSources(episodeId: string, opts?: { userAction?: boolean }): Promise<SourceList> {
    this.listCalls.push({ episodeId, userAction: opts?.userAction === true });
    return this.listScript.shift()?.() ?? Promise.resolve(this.listResult);
  }

  override addSource(episodeId: string, input: SourceInput, intent: ResearchCommandIntent): Promise<ResearchSource> {
    this.addCalls.push({ episodeId, input, key: intent.key, intent: intent.intent });
    return this.addScript.shift()?.() ?? Promise.resolve(this.addResult);
  }

  override removeSource(episodeId: string, sourceId: string): Promise<void> {
    this.removeCalls.push({ episodeId, sourceId });
    return this.removeScript.shift()?.() ?? Promise.resolve();
  }

  override retrySource(episodeId: string, sourceId: string): Promise<ResearchSource> {
    this.retryCalls.push({ episodeId, sourceId });
    return this.retryScript.shift()?.() ?? Promise.resolve(this.retryResult);
  }

  override getFactSheet(episodeId: string, opts?: { userAction?: boolean }): Promise<FactSheet | null> {
    this.sheetCalls.push({ episodeId, userAction: opts?.userAction === true });
    return this.sheetScript.shift()?.() ?? Promise.resolve(this.sheetResult);
  }

  override saveFactSheet(episodeId: string, etag: string, facts: readonly FactInput[]): Promise<FactSheetWrite> {
    this.saveCalls.push({ episodeId, etag, facts });
    return this.saveScript.shift()?.() ?? Promise.resolve(this.writeResult);
  }

  override approveFactSheet(episodeId: string, etag: string): Promise<FactSheetWrite> {
    this.approveCalls.push({ episodeId, etag });
    return this.approveScript.shift()?.() ?? Promise.resolve({ ...this.writeResult, status: 'approved' });
  }

  override generateFactSheet(episodeId: string, intent: ResearchCommandIntent): Promise<FactSheetRun> {
    this.generateCalls.push({ episodeId, key: intent.key, intent: intent.intent });
    return this.generateScript.shift()?.() ?? Promise.resolve(this.generateResult);
  }
}

export function sourceLimits(overrides: Partial<SourceLimits> = {}): SourceLimits {
  return { maxPerEpisode: 10, urlSourcesLast24h: 0, maxUrlSourcesPerDay: 20, ...overrides };
}

export function source(n: number, overrides: Partial<ResearchSource> = {}): ResearchSource {
  const kind: SourceKind = overrides.kind ?? 'url';
  return {
    id: `source-${String(n)}`,
    kind,
    url: kind === 'url' ? `https://example${String(n)}.com/article` : null,
    registrableDomain: kind === 'url' ? `example${String(n)}.com` : null,
    noteText: kind === 'user_note' ? `Note ${String(n)}` : null,
    status: 'fetched',
    errorCode: null,
    errorMessageKey: null,
    httpStatus: 200,
    title: `Title ${String(n)}`,
    finalUrl: null,
    contentType: 'text/html',
    textChars: 1200,
    isCited: false,
    createdAt: '2026-10-01T10:00:00Z',
    fetchedAt: '2026-10-01T10:00:05Z',
    ...overrides,
  };
}

export function sourceList(items: readonly ResearchSource[], limits: Partial<SourceLimits> = {}): SourceList {
  return { items, limits: sourceLimits(limits) };
}

export function fact(n: number, overrides: Partial<Fact> = {}): Fact {
  const id = overrides.id ?? `f${String(n)}`;
  const refs = overrides.sources ?? [{ sourceId: `source-${String(n)}`, alias: `S${String(n)}`, title: `Title ${String(n)}`, url: `https://example${String(n)}.com/article`, isUserNote: false }];
  return {
    id,
    statement: `Fact ${String(n)}`,
    confidence: 'high',
    sources: refs,
    sourceIds: overrides.sourceIds ?? refs.map((ref) => ref.sourceId),
    sourceOverlap: null,
    origin: 'model',
    ...overrides,
  };
}

export function factSheet(n: number, overrides: Partial<FactSheet> = {}): FactSheet {
  return {
    id: 'sheet-1',
    version: n,
    status: 'draft',
    etag: `"fs-${String(n)}"`,
    facts: [fact(1)],
    promptVersion: 'fact-sheet.v1',
    createdAt: '2026-10-01T10:00:00Z',
    approvedAt: null,
    maxFactId: 1,
    ...overrides,
  };
}

/** The 400 `fact_sheet_invalid_content` answer: `errors` names the field (`content.facts[1].sourceIds`). */
export const INVALID_CONTENT_FIELDS: Readonly<Record<string, readonly string[]>> = { 'content.facts[1].sourceIds': ['unknown_source'] };
