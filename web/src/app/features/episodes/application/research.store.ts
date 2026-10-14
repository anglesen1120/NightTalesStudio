import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';
import { AppErrorMapper } from '@core/errors';
import type { AppError, Result } from '@core/errors';
import { IdempotencyKeys } from '@core/http';
import type { IdempotencyIntent } from '@core/http';
import { RealtimeService } from '@core/realtime';
import type { RealtimeSubscription } from '@core/realtime';
import { SessionStore } from '@core/session';
import type { EpisodeDetail, SeriesSummary } from '../domain/episode.model';
import type { GateState } from '../domain/episode-gate';
import { EpisodesRepository } from '../domain/episodes.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FACT_STATEMENT_MAX_CHARS, SOURCE_DEFAULT_MAX_PER_EPISODE } from '../domain/research.model';
import type { Fact, FactConfidence, FactInput, FactSheet, FactSourceRef, ResearchSource, SourceLimits } from '../domain/research.model';
import { ResearchRepository } from '../domain/research.repository';
import type { FactSheetWrite, SourceInput } from '../domain/research.repository';
import { isAppError } from './episode-detail.store';

/** A fact in the local draft: `id` is empty until the server has stored it, so the row is tracked by `clientKey`. */
export type DraftFact = Fact & { readonly clientKey: string };

/** README D18: the dirty flag of an unsaved draft trails an edit by this much, so the tab guard never fires per keystroke. */
export const DIRTY_DEBOUNCE_MS = 150;
const RUNNING_STATUSES: readonly string[] = ['queued', 'running'];
const TRUE_CRIME_TOPIC = 'true_crime';

let nextClientKey = 0;
/** A locally added fact has no server id yet; this key is the `@for` identity until the next read replaces it. */
function newClientKey(): string {
  nextClientKey += 1;
  return `local-${String(nextClientKey)}`;
}

/** The facts as the dirty comparison sees them: id, text, confidence and citations, in list order. */
function serializeFacts(facts: readonly Fact[]): string {
  return JSON.stringify(facts.map((fact) => [fact.id, fact.statement, fact.confidence, [...fact.sourceIds]]));
}

/**
 * Page-scoped writer of `/episodes/:id/research` (10.5.5-T1). It owns one episode's sources and fact sheet, the local
 * draft of that sheet, and the `EPISODE_GATE` signal the editor route reads. The editor route provides a second
 * instance whose only job is that gate: it never writes (02.5.3/02.5.4 hand-over).
 *
 * Reads: `load`/`refresh`/`watch` are the only triggers — no polling, no timer beyond the 250 ms coalescing window the
 * realtime service owns and the 150 ms dirty debounce. A background read never replaces a draft the user has edited:
 * only `load`, `reloadLatest`, a post-write re-read and `reset` adopt a server document wholesale, so an unsaved edit
 * cannot be lost by an answer to a question the user never asked (WR12/10.5.6-T1 lesson).
 */
@Injectable()
export class ResearchStore {
  private readonly repo = inject(ResearchRepository);
  private readonly episodes = inject(EpisodesRepository);
  private readonly seriesRepo = inject(SeriesOptionsRepository);
  private readonly realtime = inject(RealtimeService);
  private readonly session = inject(SessionStore);
  private readonly mapper = inject(AppErrorMapper);
  private readonly keys = inject(IdempotencyKeys);
  private readonly ownerGeneration = this.session.generation();

  private readonly sourcesState = signal<readonly ResearchSource[]>([]);
  private readonly limitsState = signal<SourceLimits | null>(null);
  private readonly sheetState = signal<FactSheet | null>(null);
  private readonly draftState = signal<readonly DraftFact[]>([]);
  private readonly loadedState = signal(false);
  private readonly loadingState = signal(false);
  private readonly refreshingState = signal(false);
  private readonly generatingState = signal(false);
  private readonly savingState = signal(false);
  private readonly approvingState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly formErrorState = signal<AppError | null>(null);
  private readonly panelErrorState = signal<AppError | null>(null);
  private readonly conflictState = signal<{ etag: string } | null>(null);
  private readonly rateLimitState = signal<number | null>(null);
  private readonly episodeState = signal<EpisodeDetail | null>(null);
  private readonly seriesState = signal<SeriesSummary | null>(null);
  private readonly baselineJson = signal('[]');
  private readonly draftJson = signal('[]');

  readonly sources = this.sourcesState.asReadonly();
  readonly limits = this.limitsState.asReadonly();
  readonly factSheet = this.sheetState.asReadonly();
  readonly draft = this.draftState.asReadonly();
  readonly loaded = this.loadedState.asReadonly();
  readonly loading = this.loadingState.asReadonly();
  readonly refreshing = this.refreshingState.asReadonly();
  readonly generating = this.generatingState.asReadonly();
  readonly savingFactSheet = this.savingState.asReadonly();
  readonly approvingFactSheet = this.approvingState.asReadonly();
  readonly error = this.errorState.asReadonly();
  readonly formError = this.formErrorState.asReadonly();
  readonly panelError = this.panelErrorState.asReadonly();
  readonly conflict = this.conflictState.asReadonly();
  readonly rateLimitSeconds = this.rateLimitState.asReadonly();

  private id: string | null = null;
  private requestGeneration = 0;
  private destroyed = false;
  private subscription: RealtimeSubscription | null = null;
  private dirtyTimer: number | undefined;
  private addIntent: IdempotencyIntent | null = null;
  private generateIntent: IdempotencyIntent | null = null;
  private adding = false;
  private submitted: string | null = null;
  private readonly rowsInFlight = new Set<string>();

  /** The episode's live generation state; `null` before the first detail read (the panel reads it for the run states). */
  readonly generation = computed(() => this.episodeState()?.generation ?? null);
  readonly kind = computed(() => this.generation()?.kind ?? null);
  /** Any live run of the episode (Design): the panel names the kind, the build button only waits for it. */
  readonly running = computed(() => {
    const run = this.generation();
    return run !== null && run.kind !== '' && RUNNING_STATUSES.includes(run.status);
  });
  readonly pendingCount = computed(() => this.sourcesState().filter((source) => source.status === 'pending').length);
  readonly urlCount = computed(() => this.sourcesState().filter((source) => source.kind === 'url').length);
  readonly usedCount = computed(() => this.sourcesState().length);
  readonly maxCount = computed(() => this.limitsState()?.maxPerEpisode ?? SOURCE_DEFAULT_MAX_PER_EPISODE);
  /** Both caps are the server's own (`limits`): the per-episode maximum and the rolling 24 h url budget (notes still pass). */
  readonly canAddUrl = computed(() => {
    const limits = this.limitsState();
    if (limits === null) return this.urlCount() < this.maxCount();
    return this.urlCount() < limits.maxPerEpisode && limits.urlSourcesLast24h < limits.maxUrlSourcesPerDay;
  });
  readonly dirty = computed(() => this.draftJson() !== this.baselineJson());
  readonly isApproved = computed(() => this.sheetState()?.status === 'approved');
  readonly isTrueCrime = computed(() => this.seriesState()?.topic === TRUE_CRIME_TOPIC);

  private readonly invalidFacts = computed(() => this.draftState().filter((fact) => {
    const statement = fact.statement.trim();
    return statement === '' || statement.length > FACT_STATEMENT_MAX_CHARS || fact.sourceIds.length === 0;
  }));

  readonly canGenerate = computed(() =>
    this.rateLimitState() === null && this.sourcesState().some((source) => source.status === 'fetched') && !this.running() &&
    !this.generatingState() && !(this.isApproved() && !this.dirty()) && !this.savingState() && !this.approvingState());
  readonly canSave = computed(() => this.rateLimitState() === null && this.sheetState() !== null && this.dirty() &&
    !this.savingState() && !this.approvingState() && this.invalidFacts().length === 0);
  readonly canApprove = computed(() => this.rateLimitState() === null && this.sheetState()?.status === 'draft' && !this.dirty() &&
    !this.savingState() && !this.approvingState());

  /**
   * `EPISODE_GATE`, mirroring 02 F13 for copy only (the server's 409 stays the authority). An instance that has not
   * completed its first successful load reports `open`, so the editor route can never pre-disable the story button
   * before it knows anything.
   */
  readonly gate = computed<GateState>(() => {
    if (!this.loadedState()) return 'open';
    const sheet = this.sheetState();
    if (!this.isTrueCrime()) return 'not_applicable';
    if (sheet === null || sheet.status !== 'approved') return 'sheet_not_approved';
    const cited = new Set(sheet.facts.flatMap((fact) => [...fact.sourceIds]));
    const fetched = this.sourcesState().filter((source) => source.kind === 'url' && source.status === 'fetched' && cited.has(source.id));
    return fetched.length >= 2 ? 'open' : 'sources_insufficient';
  });

  /** The existing row a 409 `source_duplicate` names, when the submitter's value matches it; cleared on edit. */
  readonly duplicateOf = computed<string | null>(() => {
    if (this.formErrorState()?.code !== 'source_duplicate') return null;
    const value = this.submitted;
    if (value === null) return null;
    return this.sourcesState().find((source) => source.url === value || source.finalUrl === value)?.id ?? null;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.requestGeneration += 1;
      window.clearTimeout(this.dirtyTimer);
      this.subscription?.unsubscribe();
      this.subscription = null;
      this.addIntent = null;
      this.generateIntent = null;
      this.rowsInFlight.clear();
    });
    effect(() => {
      if (this.session.generation() !== this.ownerGeneration) this.reset();
    });
  }

  /** First load of an episode: the two research reads in parallel, then the episode/series context of the gate. */
  async load(episodeId: string): Promise<void> {
    if (episodeId === '' || this.destroyed) return;
    if (episodeId === this.id && this.loadedState()) return;
    this.requestGeneration += 1;
    const generation = this.requestGeneration;
    this.id = episodeId;
    this.loadedState.set(false);
    this.loadingState.set(true);
    this.errorState.set(null);
    this.panelErrorState.set(null);
    this.formErrorState.set(null);
    this.conflictState.set(null);
    this.adoptSheet(null);
    const ok = await this.readResearch(episodeId, generation, { userAction: true, series: true, silent: false });
    if (!this.current(generation)) return;
    this.loadingState.set(false);
    this.loadedState.set(ok);
  }

  /** The manual "Refresh status" (a user action): the same reads, without touching the loaded flag. */
  async refresh(episodeId: string): Promise<Result<void>> {
    if (this.refreshingState() || this.destroyed) return this.refused();
    this.refreshingState.set(true);
    const generation = this.requestGeneration;
    const ok = await this.readResearch(episodeId, generation, { userAction: true, series: true, silent: false });
    if (this.current(generation)) this.refreshingState.set(false);
    if (ok) return { ok: true, value: undefined };
    return { ok: false, error: this.errorState() ?? this.mapper.from(null) };
  }

  /**
   * Realtime: the service already collapses every event about this episode — and every (re)connect resync — into one
   * call per 250 ms window (realtime.service.ts:216), so this handler starts no timer of its own. A payload carries ids
   * only (BD11/BD16), so an event is not classified: sources, sheet and the generation state are re-read together.
   */
  watch(episodeId: string): void {
    this.subscription?.unsubscribe();
    this.subscription = null;
    if (episodeId === '') return;
    this.subscription = this.realtime.subscribeEpisode(episodeId, () => {
      if (this.destroyed || this.id !== episodeId) return;
      // A background read is silent (its failure must not raise the page error state) and never reloads the series.
      void this.readResearch(episodeId, this.requestGeneration, { userAction: false, series: false, silent: true });
    });
  }

  async addUrl(episodeId: string, url: string): Promise<Result<void>> {
    return this.add(episodeId, { url });
  }

  async addNote(episodeId: string, noteText: string): Promise<Result<void>> {
    return this.add(episodeId, { noteText });
  }

  private async add(episodeId: string, input: SourceInput): Promise<Result<void>> {
    if (this.adding || this.destroyed) return this.refused();
    this.adding = true;
    this.submitted = input.url ?? input.noteText ?? null;
    this.formErrorState.set(null);
    // One intent per submit: a retry after a network failure reuses the key, any other answer settles it.
    this.addIntent ??= this.keys.begin('source.add');
    const intent = this.addIntent;
    try {
      await this.repo.addSource(episodeId, input, intent);
      intent.settle();
      this.addIntent = null;
      this.submitted = null;
      // The rows and the 24 h counters are the server's: the write is followed by one list read, never by a local guess.
      await this.readSources(episodeId, this.requestGeneration, false, false);
      return { ok: true, value: undefined };
    } catch (cause) {
      const error = this.toError(cause);
      // A network failure (status 0) is the one retryable answer: the key stays open so the next submit replays it.
      if (error.status !== 0) {
        intent.settle();
        this.addIntent = null;
      }
      this.formErrorState.set(error);
      this.captureRateLimit(error);
      return { ok: false, error };
    } finally {
      this.adding = false;
    }
  }

  async remove(episodeId: string, sourceId: string): Promise<Result<void>> {
    if (this.rowsInFlight.has(sourceId) || this.destroyed || this.rateLimitState() !== null) return this.refused();
    this.rowsInFlight.add(sourceId);
    this.panelErrorState.set(null);
    try {
      await this.repo.removeSource(episodeId, sourceId);
      await this.readSources(episodeId, this.requestGeneration, false, false);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failRow(cause) };
    } finally {
      this.rowsInFlight.delete(sourceId);
    }
  }

  async retry(episodeId: string, sourceId: string): Promise<Result<void>> {
    if (this.rowsInFlight.has(sourceId) || this.destroyed || this.rateLimitState() !== null) return this.refused();
    this.rowsInFlight.add(sourceId);
    this.panelErrorState.set(null);
    try {
      await this.repo.retrySource(episodeId, sourceId);
      await this.readSources(episodeId, this.requestGeneration, false, false);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failRow(cause) };
    } finally {
      this.rowsInFlight.delete(sourceId);
    }
  }

  /** `POST …/fact-sheet/generate` (202). One intent per click; a retry after a network failure reuses it (10.2.3-T1). */
  async generate(episodeId: string): Promise<Result<void>> {
    if (!this.canGenerate() || this.destroyed) return this.refused();
    this.generateIntent ??= this.keys.begin('fact_sheet.generate');
    const intent = this.generateIntent;
    this.generatingState.set(true);
    this.panelErrorState.set(null);
    try {
      await this.repo.generateFactSheet(episodeId, intent);
      intent.settle();
      this.generateIntent = null;
      return { ok: true, value: undefined };
    } catch (cause) {
      const error = this.toError(cause);
      // A network failure (status 0) is the one retryable answer: the key stays open so "Retry" replays this click.
      if (error.status === 0) this.generateIntent = intent;
      else {
        intent.settle();
        this.generateIntent = null;
      }
      return { ok: false, error: this.failPanel(error) };
    } finally {
      this.generatingState.set(false);
    }
  }

  /** Local draft only: the statement, its confidence and its cited sources. Nothing is sent before `save`. */
  editFact(factId: string, patch: Partial<Pick<Fact, 'statement' | 'confidence' | 'sourceIds'>>): void {
    this.updateFact(factId, (fact) => ({
      ...fact,
      ...(patch.statement === undefined ? {} : { statement: patch.statement }),
      ...(patch.confidence === undefined ? {} : { confidence: patch.confidence }),
      ...(patch.sourceIds === undefined ? {} : { sourceIds: [...new Set(patch.sourceIds)], sources: this.refsFor([...new Set(patch.sourceIds)]) }),
      // A fact the owner touched is theirs from here on; the server keeps the origin of the row it already has.
      origin: fact.origin,
    }));
  }

  /** A new fact starts as the owner's own: `id ''` (the server assigns one), origin `user`, confidence `high`. */
  addFact(sourceIds: readonly string[]): void {
    const sheet = this.sheetState();
    if (sheet === null || sheet.status !== 'draft' || this.destroyed) return;
    const cited = [...new Set(sourceIds.filter((sourceId) => sourceId !== ''))];
    if (cited.length === 0) return;
    this.draftState.update((facts) => [...facts, {
      id: '',
      statement: '',
      confidence: 'high',
      sources: this.refsFor(cited),
      sourceIds: cited,
      sourceOverlap: null,
      origin: 'user',
      clientKey: newClientKey(),
    }]);
    this.markEdited();
  }

  removeFact(factId: string): void {
    if (this.sheetState()?.status !== 'draft' || this.destroyed) return;
    this.draftState.update((facts) => facts.filter((fact) => !this.isFact(fact, factId)));
    this.markEdited();
  }

  /** 02.5.4 `:73`: the owner drops one citation of a fact the server rejected; the rest of the fact stays. */
  dropSourceFromFact(factId: string, sourceId: string): void {
    this.updateFact(factId, (fact) => {
      const sourceIds = fact.sourceIds.filter((candidate) => candidate !== sourceId);
      return { ...fact, sourceIds, sources: this.refsFor(sourceIds) };
    });
  }

  /** "Edit" on an approved sheet: a PUT of the current facts creates the next draft version (02.5.4-T1:26). */
  async startEdit(episodeId: string): Promise<Result<void>> {
    const sheet = this.sheetState();
    if (sheet === null || sheet.status !== 'approved' || this.savingState() || this.approvingState() || this.destroyed) return this.refused();
    return this.write(episodeId, sheet.etag, () => this.repo.saveFactSheet(episodeId, sheet.etag, this.buildInputs()));
  }

  async save(episodeId: string): Promise<Result<void>> {
    const sheet = this.sheetState();
    if (!this.canSave() || sheet === null || this.destroyed) return this.refused();
    return this.write(episodeId, sheet.etag, () => this.repo.saveFactSheet(episodeId, sheet.etag, this.buildInputs()));
  }

  async approve(episodeId: string): Promise<Result<void>> {
    const sheet = this.sheetState();
    if (!this.canApprove() || sheet === null || this.destroyed) return this.refused();
    return this.write(episodeId, sheet.etag, () => this.repo.approveFactSheet(episodeId, sheet.etag));
  }

  /** Conflict dialog, "Reload latest": the server's version wins and the local draft is dropped. */
  async reloadLatest(episodeId: string): Promise<Result<void>> {
    const generation = this.requestGeneration;
    try {
      const sheet = await this.repo.getFactSheet(episodeId, { userAction: true });
      if (!this.current(generation) || this.id !== episodeId) return this.refused();
      this.adoptSheet(sheet);
      this.conflictState.set(null);
      this.panelErrorState.set(null);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failPanel(this.toError(cause)) };
    }
  }

  /** Conflict dialog, "Save mine as new version": the local draft is re-sent against the fresh ETag, so the server's
   * semantics create the next draft version instead of overwriting the other tab's write (02.5.4-T1:26). */
  async saveMineAsNewVersion(episodeId: string): Promise<Result<void>> {
    if (this.destroyed) return this.refused();
    const generation = this.requestGeneration;
    let etag: string;
    try {
      const latest = await this.repo.getFactSheet(episodeId, { userAction: true });
      if (!this.current(generation) || this.id !== episodeId) return this.refused();
      if (latest === null) return { ok: false, error: this.failPanel(this.mapper.fromProblem(null, 404, null)) };
      etag = latest.etag;
    } catch (cause) {
      return { ok: false, error: this.failPanel(this.toError(cause)) };
    }
    // The local draft stays exactly as the user left it: only the concurrency token is refreshed.
    return this.write(episodeId, etag, () => this.repo.saveFactSheet(episodeId, etag, this.buildInputs()));
  }

  clearFormError(): void {
    this.formErrorState.set(null);
    this.submitted = null;
    // An edited value is a new intent: the next submit mints its own key rather than replaying the failed one.
    this.addIntent = null;
  }

  clearPanelError(): void {
    this.panelErrorState.set(null);
    this.rateLimitState.set(null);
  }

  /** Drops every draft and read of this instance (a leave, a logout, a session end). */
  reset(): void {
    this.requestGeneration += 1;
    this.id = null;
    window.clearTimeout(this.dirtyTimer);
    this.dirtyTimer = undefined;
    this.adoptSheet(null);
    this.sourcesState.set([]);
    this.limitsState.set(null);
    this.episodeState.set(null);
    this.seriesState.set(null);
    this.loadedState.set(false);
    this.loadingState.set(false);
    this.refreshingState.set(false);
    this.generatingState.set(false);
    this.savingState.set(false);
    this.approvingState.set(false);
    this.errorState.set(null);
    this.formErrorState.set(null);
    this.panelErrorState.set(null);
    this.conflictState.set(null);
    this.rateLimitState.set(null);
    this.addIntent = null;
    this.generateIntent = null;
    this.rowsInFlight.clear();
    this.submitted = null;
  }

  /** Sources + fact sheet (parallel), then the episode and series context of the gate. `true` when both reads answered. */
  private async readResearch(episodeId: string, generation: number, opts: { userAction: boolean; series: boolean; silent: boolean }): Promise<boolean> {
    // Cleared once, before the pair: the two reads answer in parallel, so a success must never erase the other's failure.
    if (!opts.silent) this.errorState.set(null);
    const [sourcesOk, sheetOk] = await Promise.all([
      this.readSources(episodeId, generation, opts.userAction, opts.silent),
      this.readSheet(episodeId, generation, opts.userAction, opts.silent),
    ]);
    await this.readEpisode(episodeId, generation, opts.series);
    return sourcesOk && sheetOk;
  }

  private async readSources(episodeId: string, generation: number, userAction: boolean, silent: boolean): Promise<boolean> {
    try {
      const list = await this.repo.listSources(episodeId, { userAction });
      if (!this.current(generation) || this.id !== episodeId) return false;
      this.sourcesState.set(list.items);
      this.limitsState.set(list.limits);
      return true;
    } catch (cause) {
      if (!this.current(generation) || this.id !== episodeId) return false;
      if (!silent) this.errorState.set(this.toError(cause));
      return false;
    }
  }

  private async readSheet(episodeId: string, generation: number, userAction: boolean, silent: boolean): Promise<boolean> {
    try {
      const sheet = await this.repo.getFactSheet(episodeId, { userAction });
      if (!this.current(generation) || this.id !== episodeId) return false;
      // A read never replaces an edited draft: the local facts (and the ETag that describes them) stay until the write
      // path resolves the divergence through the 412 conflict dialog.
      if (this.dirty()) return true;
      this.adoptSheet(sheet);
      return true;
    } catch (cause) {
      if (!this.current(generation) || this.id !== episodeId) return false;
      if (!silent) this.errorState.set(this.toError(cause));
      return false;
    }
  }

  private async readEpisode(episodeId: string, generation: number, series: boolean): Promise<void> {
    let detail: EpisodeDetail;
    try {
      detail = await this.episodes.get(episodeId, { userAction: false });
    } catch {
      // The gate then reads as `open` (never a false block) and the server's 409 stays the authority (10.5.2-T1).
      return;
    }
    if (!this.current(generation) || this.id !== episodeId) return;
    this.episodeState.set(detail);
    if (!series || detail.seriesId === '') return;
    try {
      const summary = await this.seriesRepo.get(detail.seriesId);
      if (!this.current(generation) || this.id !== episodeId) return;
      this.seriesState.set(summary);
    } catch {
      // An unreadable series leaves the topic unknown, and an unknown topic never blocks the editor.
    }
  }

  /** One write path for save/approve/start-edit: send with `If-Match`, adopt the answer, then re-read the document. */
  private async write(episodeId: string, etag: string, call: () => Promise<FactSheetWrite>): Promise<Result<void>> {
    this.savingState.set(true);
    this.panelErrorState.set(null);
    this.conflictState.set(null);
    try {
      const write = await call();
      this.adoptWrite(write);
      this.captureRateLimit(null);
      // The summary answer carries no facts: a re-read is what learns the ids the server assigned (and its order).
      await this.reloadAfterWrite(episodeId);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failWrite(cause, etag) };
    } finally {
      this.savingState.set(false);
    }
  }

  private async reloadAfterWrite(episodeId: string): Promise<void> {
    const generation = this.requestGeneration;
    try {
      const sheet = await this.repo.getFactSheet(episodeId, { userAction: false });
      if (!this.current(generation) || this.id !== episodeId) return;
      this.adoptSheet(sheet);
    } catch (cause) {
      if (!this.current(generation) || this.id !== episodeId) return;
      // The write itself landed (its ETag is already adopted): only this read failed, so it is a banner, not a lost save.
      this.panelErrorState.set(this.toError(cause));
    }
  }

  /** A fact-sheet document wins wholesale: draft, baseline and the debounced dirty flag are all reset together. */
  private adoptSheet(sheet: FactSheet | null): void {
    window.clearTimeout(this.dirtyTimer);
    this.dirtyTimer = undefined;
    this.sheetState.set(sheet);
    const facts = sheet?.facts ?? [];
    this.draftState.set(facts.map((fact) => ({ ...fact, clientKey: newClientKey() })));
    const json = serializeFacts(facts);
    this.baselineJson.set(json);
    this.draftJson.set(json);
  }

  private adoptWrite(write: FactSheetWrite): void {
    const sheet = this.sheetState();
    if (sheet === null) return;
    // The answer's status/version/ETag describe the version it just installed; the facts are re-read right after.
    this.sheetState.set({ ...sheet, version: write.version, status: write.status === 'approved' ? 'approved' : 'draft', etag: write.etag });
  }

  private buildInputs(): readonly FactInput[] {
    const saved = this.sheetState()?.facts ?? [];
    return this.draftState().map((fact) => {
      const stored = saved.find((candidate) => candidate.id === fact.id);
      const input: { id?: string; statement: string; sourceIds: readonly string[]; confidence?: FactConfidence } = {
        statement: fact.statement.trim(),
        sourceIds: [...fact.sourceIds],
      };
      if (fact.id !== '') input.id = fact.id;
      // `confidence` travels only when the owner changed it; a stored fact keeps the server's own value (02.5.4-T1 T53).
      if (stored === undefined || stored.confidence !== fact.confidence) input.confidence = fact.confidence;
      return input;
    });
  }

  private updateFact(factId: string, patch: (fact: DraftFact) => DraftFact): void {
    if (this.destroyed) return;
    const sheet = this.sheetState();
    if (sheet === null || sheet.status !== 'draft') return;
    this.draftState.update((facts) => facts.map((fact) => (this.isFact(fact, factId) ? patch(fact) : fact)));
    this.markEdited();
  }

  private isFact(fact: DraftFact, factId: string): boolean {
    return factId !== '' && (fact.id === factId || fact.clientKey === factId);
  }

  /** Citation refs for the local draft: aliases follow the server's own `S<n>` list order, replaced at the next read. */
  private refsFor(sourceIds: readonly string[]): readonly FactSourceRef[] {
    const wanted = new Set(sourceIds);
    return this.sourcesState().flatMap((source, index) => wanted.has(source.id) ? [{
      sourceId: source.id,
      alias: `S${String(index + 1)}`,
      title: source.title ?? source.registrableDomain ?? '',
      url: source.url,
      isUserNote: source.kind === 'user_note',
    }] : []);
  }

  /** D18: the dirty flag trails an edit by 150 ms; an adopted document sets it at once (never a stale "unsaved"). */
  private markEdited(): void {
    window.clearTimeout(this.dirtyTimer);
    const generation = this.requestGeneration;
    this.dirtyTimer = window.setTimeout(() => {
      this.dirtyTimer = undefined;
      if (this.current(generation)) this.draftJson.set(serializeFacts(this.draftState()));
    }, DIRTY_DEBOUNCE_MS);
  }

  private failRow(cause: unknown): AppError {
    const error = this.toError(cause);
    // Row actions have no inline field: the banner is the one place that can explain a refused remove/retry.
    this.panelErrorState.set(error);
    this.captureRateLimit(error);
    return error;
  }

  private failPanel(error: AppError): AppError {
    this.panelErrorState.set(error);
    this.captureRateLimit(error);
    return error;
  }

  private failWrite(cause: unknown, etag: string): AppError {
    const error = this.toError(cause);
    // 412 and 428 are concurrency answers, not banners: the ConflictDialog offers Reload latest / Save mine as new
    // version, and only its "Save mine" re-PUTs (the ETag it was refused with is the one to report).
    if (error.status === 412 || error.status === 428) {
      this.conflictState.set({ etag });
      if (error.status === 428) this.panelErrorState.set(error);
      return error;
    }
    return this.failPanel(error);
  }

  private captureRateLimit(error: AppError | null): void {
    if (error === null) {
      this.rateLimitState.set(null);
      return;
    }
    if (error.retryAfterSec !== undefined) this.rateLimitState.set(error.retryAfterSec);
  }

  /** A refused call is a no-op: it reports the live error of its own surface, or the mapper's plain `unknown_error`
   * marker — never a fabricated HTTP status. The surfaces render the reason from the exposed flags themselves. */
  private refused(): Result<void> {
    const live = this.panelErrorState() ?? this.formErrorState() ?? this.errorState();
    return { ok: false, error: live ?? this.mapper.from(null) };
  }

  private toError(cause: unknown): AppError {
    return isAppError(cause) ? cause : this.mapper.from(cause);
  }

  private current(generation: number): boolean {
    return !this.destroyed && generation === this.requestGeneration && this.session.generation() === this.ownerGeneration;
  }
}
