import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
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
import type { Fact, FactConfidence, FactInput, FactSheet, FactSheetRun, FactSourceRef, ResearchSource, SourceLimits } from '../domain/research.model';
import { ResearchRepository } from '../domain/research.repository';
import type { FactSheetWrite, SourceInput } from '../domain/research.repository';
import { isAppError } from './episode-detail.store';

/** A fact in the local draft: `id` is empty until the server has stored it, so the row is tracked by `clientKey`. */
export type DraftFact = Fact & { readonly clientKey: string };

/** The write that an open 412 came from (F5): the conflict dialog needs it to decide what "save mine" may do. */
export type ConflictSource = 'save' | 'approve' | 'edit';

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

/** Every field a row renders, in a fixed order: two lists that differ in nothing the user sees are the same list. */
function sourceSignature(source: ResearchSource): string {
  return [
    source.id, source.kind, source.url, source.finalUrl, source.registrableDomain, source.noteText, source.status,
    source.errorCode, source.errorMessageKey, source.httpStatus, source.title, source.contentType, source.textChars,
    source.isCited, source.createdAt, source.fetchedAt,
  ].join('\u0000');
}

/** Structural comparison of two source lists (perf: a no-op refetch must not re-render every row). */
function sameSources(next: readonly ResearchSource[], current: readonly ResearchSource[]): boolean {
  return next.length === current.length &&
    next.every((source, index) => sourceSignature(source) === sourceSignature(current[index] as ResearchSource));
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
  private readonly conflictSourceState = signal<ConflictSource | null>(null);
  private readonly rateLimitState = signal<number | null>(null);
  private readonly formRateLimitState = signal<number | null>(null);
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
  /** Which write the open 412 came from: `edit` = "Edit" on an approved sheet, `save` = save/save-mine, `approve`. */
  readonly conflictSource = this.conflictSourceState.asReadonly();
  /** A page-wide wait (F2): a 429 of generate/save/approve/remove/retry holds every write of the page. */
  readonly rateLimitSeconds = this.rateLimitState.asReadonly();
  /** The add form's own wait: a 429 of one submit holds that form only, so a url wait never locks the sheet path. */
  readonly formRateLimitSeconds = this.formRateLimitState.asReadonly();

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
    !this.generatingState() && !(this.isApproved() && !this.dirtyNow()) && !this.savingState() && !this.approvingState());
  readonly canSave = computed(() => this.rateLimitState() === null && this.sheetState() !== null && this.dirtyNow() &&
    !this.savingState() && !this.approvingState() && this.invalidFacts().length === 0);
  readonly canApprove = computed(() => this.rateLimitState() === null && this.sheetState()?.status === 'draft' && !this.dirtyNow() &&
    !this.savingState() && !this.approvingState());

  /**
   * `EPISODE_GATE`, mirroring 02 F13 for copy only (the server's 409 stays the authority). An instance that has not
   * completed its first successful load reports `open`, so the editor route can never pre-disable the story button
   * before it knows anything.
   */
  readonly gate = computed<GateState>(() => {
    if (!this.loadedState()) return 'open';
    const sheet = this.sheetState();
    // The topic is the series' answer (P2): until it arrives the gate stays open, so a slow read never blocks the editor.
    if (this.seriesState() === null) return 'open';
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
      // Untracked: reset() reads and rewrites the draft, so tracking it would re-run this effect forever (a hung page).
      if (this.session.generation() !== this.ownerGeneration) untracked(() => this.reset());
    });
  }

  /** First load of an episode: the two research reads in parallel, then the episode/series context of the gate. */
  async load(episodeId: string): Promise<void> {
    if (episodeId === '' || this.destroyed) return;
    if (episodeId === this.id && this.loadedState()) return;
    this.requestGeneration += 1;
    const generation = this.requestGeneration;
    // F4: the open intents and the pending submit belong to the episode we are leaving (reset() drops them the same way).
    if (episodeId !== this.id) {
      this.addIntent = null;
      this.generateIntent = null;
      this.submitted = null;
      this.formRateLimitState.set(null);
    }
    this.id = episodeId;
    this.loadedState.set(false);
    this.loadingState.set(true);
    this.errorState.set(null);
    this.panelErrorState.set(null);
    this.formErrorState.set(null);
    this.clearConflict();
    this.adoptSheet(null);
    // A pending write/read of the episode we are leaving is stale from here on, so its flags come down with it.
    this.refreshingState.set(false);
    this.generatingState.set(false);
    this.savingState.set(false);
    this.approvingState.set(false);
    this.rowsInFlight.clear();
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
    const generation = this.requestGeneration;
    try {
      await this.repo.addSource(episodeId, input, intent);
      // The intent is bookkeeping rather than page state: it settles even when the answer arrives after a navigation.
      intent.settle();
      this.addIntent = null;
      this.submitted = null;
      if (!this.current(generation) || this.id !== episodeId) return this.refused();
      // A landed submit is the end of the wait the previous 429 announced.
      this.captureRateLimit(null, 'form');
      // The rows and the 24 h counters are the server's: the write is followed by one list read, never by a local guess.
      await this.readSources(episodeId, generation, false, false);
      return { ok: true, value: undefined };
    } catch (cause) {
      const error = this.toError(cause);
      // A network failure (status 0) is the one retryable answer: the key stays open so the next submit replays it.
      if (error.status !== 0) {
        intent.settle();
        this.addIntent = null;
      }
      // A late failure of another episode's submit is not this page's error: it never reaches a surface.
      if (this.current(generation) && this.id === episodeId) {
        this.formErrorState.set(error);
        // F2: the wait of one refused submit holds the add form only — generate/save/approve/remove/retry stay live.
        this.captureRateLimit(error, 'form');
      }
      return { ok: false, error };
    } finally {
      this.adding = false;
    }
  }

  async remove(episodeId: string, sourceId: string): Promise<Result<void>> {
    if (this.rowsInFlight.has(sourceId) || this.destroyed || this.rateLimitState() !== null) return this.refused();
    this.rowsInFlight.add(sourceId);
    this.panelErrorState.set(null);
    const generation = this.requestGeneration;
    try {
      await this.repo.removeSource(episodeId, sourceId);
      await this.readSources(episodeId, generation, false, false);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failRow(cause, episodeId, generation) };
    } finally {
      this.rowsInFlight.delete(sourceId);
    }
  }

  async retry(episodeId: string, sourceId: string): Promise<Result<void>> {
    if (this.rowsInFlight.has(sourceId) || this.destroyed || this.rateLimitState() !== null) return this.refused();
    this.rowsInFlight.add(sourceId);
    this.panelErrorState.set(null);
    const generation = this.requestGeneration;
    try {
      await this.repo.retrySource(episodeId, sourceId);
      await this.readSources(episodeId, generation, false, false);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failRow(cause, episodeId, generation) };
    } finally {
      this.rowsInFlight.delete(sourceId);
    }
  }

  /** `POST …/fact-sheet/generate` (202). One intent per click; a retry after a network failure reuses it (10.2.3-T1). */
  async generate(episodeId: string): Promise<Result<void>> {
    if (!this.canGenerate() || this.destroyed) return this.refused();
    this.generateIntent ??= this.keys.begin('fact_sheet.generate');
    const intent = this.generateIntent;
    const generation = this.requestGeneration;
    this.generatingState.set(true);
    this.panelErrorState.set(null);
    try {
      const run = await this.repo.generateFactSheet(episodeId, intent);
      // Bookkeeping first: the key settles even when the answer arrives after the page moved to another episode.
      intent.settle();
      this.generateIntent = null;
      if (!this.current(generation) || this.id !== episodeId) return this.refused();
      // F3: the 202 is the truth about the run until the next read — the build button must hold from this answer on.
      this.applyRun(episodeId, run);
      return { ok: true, value: undefined };
    } catch (cause) {
      const error = this.toError(cause);
      // A network failure (status 0) is the one retryable answer: the key stays open so "Retry" replays this click.
      if (error.status === 0) this.generateIntent = intent;
      else {
        intent.settle();
        this.generateIntent = null;
      }
      return { ok: false, error: this.failPanel(error, episodeId, generation) };
    } finally {
      if (this.current(generation)) this.generatingState.set(false);
    }
  }

  /** Local draft only: the statement, its confidence and its cited sources. Nothing is sent before `save`. */
  editFact(factId: string, patch: Partial<Pick<Fact, 'statement' | 'confidence' | 'sourceIds'>>): void {
    this.updateFact(factId, (fact) => ({
      ...fact,
      ...(patch.statement === undefined ? {} : { statement: patch.statement }),
      ...(patch.confidence === undefined ? {} : { confidence: patch.confidence }),
      ...(patch.sourceIds === undefined ? {} : { sourceIds: [...new Set(patch.sourceIds)], sources: this.refsFor([...new Set(patch.sourceIds)]) }),
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
    this.retireInvalidContentError();
    this.markEdited();
  }

  removeFact(factId: string): void {
    if (this.sheetState()?.status !== 'draft' || this.destroyed) return;
    this.draftState.update((facts) => facts.filter((fact) => !this.isFact(fact, factId)));
    // The fact the refusal named is gone: the pointers that indexed it are gone with it.
    this.retireInvalidContentError();
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
    return this.write(episodeId, sheet.etag, () => this.repo.saveFactSheet(episodeId, sheet.etag, this.buildInputs()), 'saving', 'edit');
  }

  async save(episodeId: string): Promise<Result<void>> {
    const sheet = this.sheetState();
    if (!this.canSave() || sheet === null || this.destroyed) return this.refused();
    return this.write(episodeId, sheet.etag, () => this.repo.saveFactSheet(episodeId, sheet.etag, this.buildInputs()));
  }

  async approve(episodeId: string): Promise<Result<void>> {
    const sheet = this.sheetState();
    if (!this.canApprove() || sheet === null || this.destroyed) return this.refused();
    return this.write(episodeId, sheet.etag, () => this.repo.approveFactSheet(episodeId, sheet.etag), 'approving', 'approve');
  }

  /** Conflict dialog, "Reload latest": the server's version wins and the local draft is dropped. */
  async reloadLatest(episodeId: string): Promise<Result<void>> {
    const generation = this.requestGeneration;
    try {
      const sheet = await this.repo.getFactSheet(episodeId, { userAction: true });
      if (!this.current(generation) || this.id !== episodeId) return this.refused();
      this.adoptSheet(sheet);
      this.clearConflict();
      this.panelErrorState.set(null);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failPanel(this.toError(cause), episodeId, generation) };
    }
  }

  /** Conflict dialog, "Save mine as new version": the local draft is re-sent against the fresh ETag, so the server's
   * semantics create the next draft version instead of overwriting the other tab's write (02.5.4-T1:26). */
  async saveMineAsNewVersion(episodeId: string): Promise<Result<void>> {
    // F5: an approve/edit conflict means the local document *is* the server's — there is nothing of mine to re-send, so
    // this is refused unless the owner has since edited the draft (the dialog's other button, Reload latest, fits).
    if (this.destroyed || !this.dirtyNow()) return this.refused();
    const generation = this.requestGeneration;
    let etag: string;
    try {
      const latest = await this.repo.getFactSheet(episodeId, { userAction: true });
      if (!this.current(generation) || this.id !== episodeId) return this.refused();
      if (latest === null) return { ok: false, error: this.failPanel(this.mapper.fromProblem(null, 404, null), episodeId, generation) };
      etag = latest.etag;
    } catch (cause) {
      return { ok: false, error: this.failPanel(this.toError(cause), episodeId, generation) };
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

  /** The add form's countdown elapsed: the wait is over, so the form accepts input again. */
  clearFormRateLimit(): void {
    this.formRateLimitState.set(null);
    this.clearFormError();
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
    this.clearConflict();
    this.rateLimitState.set(null);
    this.formRateLimitState.set(null);
    this.addIntent = null;
    this.generateIntent = null;
    this.rowsInFlight.clear();
    this.submitted = null;
  }

  /** Sources + fact sheet + the episode detail (all three in parallel), then the series context of the gate. */
  private async readResearch(episodeId: string, generation: number, opts: { userAction: boolean; series: boolean; silent: boolean }): Promise<boolean> {
    // Cleared once, before the trio: the reads answer in parallel, so a success must never erase the other's failure.
    if (!opts.silent) this.errorState.set(null);
    // P2: the episode GET joins the pair, so the gate's own read costs no extra round trip; only the series chains after
    // it (its id comes from the detail).
    const [sourcesOk, sheetOk, detail] = await Promise.all([
      this.readSources(episodeId, generation, opts.userAction, opts.silent),
      this.readSheet(episodeId, generation, opts.userAction, opts.silent),
      this.readEpisodeDetail(episodeId, generation),
    ]);
    if (detail !== null && opts.series && detail.seriesId !== '') await this.readSeries(detail.seriesId, episodeId, generation);
    return sourcesOk && sheetOk;
  }

  private async readSources(episodeId: string, generation: number, userAction: boolean, silent: boolean): Promise<boolean> {
    try {
      const list = await this.repo.listSources(episodeId, { userAction });
      if (!this.current(generation) || this.id !== episodeId) return false;
      // P1: `[sources]` keeps its identity when the list is the same, so a no-op refetch re-renders no row.
      if (!sameSources(list.items, this.sourcesState())) this.sourcesState.set(list.items);
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
      // path resolves the divergence through the 412 conflict dialog. F7: an edit inside the 150 ms debounce window is
      // an edit — the comparison that decides here never waits for the trailing flag.
      if (this.dirtyNow()) return true;
      // P1: the same document under the same ETag is not a change; adopting it would re-key every row for nothing.
      if (this.sameSheet(sheet)) return true;
      this.adoptSheet(sheet);
      return true;
    } catch (cause) {
      if (!this.current(generation) || this.id !== episodeId) return false;
      if (!silent) this.errorState.set(this.toError(cause));
      return false;
    }
  }

  /** The episode detail carries the live run state the panel reads; a failure leaves the gate `open` (never a block). */
  private async readEpisodeDetail(episodeId: string, generation: number): Promise<EpisodeDetail | null> {
    try {
      const detail = await this.episodes.get(episodeId, { userAction: false });
      if (!this.current(generation) || this.id !== episodeId) return null;
      this.episodeState.set(detail);
      return detail;
    } catch {
      return null;
    }
  }

  private async readSeries(seriesId: string, episodeId: string, generation: number): Promise<void> {
    try {
      const summary = await this.seriesRepo.get(seriesId);
      if (!this.current(generation) || this.id !== episodeId) return;
      this.seriesState.set(summary);
    } catch {
      // An unreadable series leaves the topic unknown, and an unknown topic never blocks the editor.
    }
  }

  /** F3: the run a 202 named, until the next detail read replaces it (the episode's generation signal is that state). */
  private applyRun(episodeId: string, run: FactSheetRun): void {
    const detail = this.episodeState();
    if (detail === null) {
      // No detail to update (its read failed): one read is what tells the panel a run exists — never a local guess.
      void this.readEpisodeDetail(episodeId, this.requestGeneration);
      return;
    }
    // The generate run is `generation_runs.kind = fact_sheet` (02.5.3:34). The fields the 202 does not carry are the
    // ones a freshly queued run has (the next detail read replaces them with the server's own values).
    this.episodeState.set({
      ...detail,
      generation: {
        kind: 'fact_sheet',
        status: run.status === '' ? 'queued' : run.status,
        stage: 'queued',
        errorCode: null,
        startedAt: null,
        processingPaused: null,
      },
    });
  }

  /** The same document, fact for fact: the ETag is the version identity, the facts are what the page renders. */
  private sameSheet(sheet: FactSheet | null): boolean {
    const current = this.sheetState();
    if (current === null || sheet === null) return current === sheet;
    return sheet.etag === current.etag && sheet.status === current.status && sheet.version === current.version &&
      serializeFacts(sheet.facts) === serializeFacts(current.facts);
  }

  /**
   * One write path for save/approve/start-edit: send with `If-Match`, adopt the answer, then re-read the document.
   * The phase picks the in-flight flag the surfaces read (`savingFactSheet` / `approvingFactSheet`), so an approve
   * that is mid-request is visible to the panel's busy state and to the page's conflict guard alike.
   */
  private async write(
    episodeId: string, etag: string, call: () => Promise<FactSheetWrite>,
    phase: 'saving' | 'approving' = 'saving', source: ConflictSource = 'save',
  ): Promise<Result<void>> {
    const inFlight = phase === 'approving' ? this.approvingState : this.savingState;
    const generation = this.requestGeneration;
    inFlight.set(true);
    this.panelErrorState.set(null);
    this.clearConflict();
    try {
      const write = await call();
      // A stale answer is dropped whole: no adopted version, ETag or cleared rate limit lands on the episode the page
      // shows now — the same rule the two reads follow.
      if (!this.current(generation) || this.id !== episodeId) return this.refused();
      this.adoptWrite(write);
      this.captureRateLimit(null);
      // The summary answer carries no facts: a re-read is what learns the ids the server assigned (and its order).
      await this.reloadAfterWrite(episodeId);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failWrite(cause, etag, episodeId, generation, source) };
    } finally {
      if (this.current(generation)) inFlight.set(false);
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
    // P1: a stored fact keeps the row key it already had, so a read that brings the same facts back neither re-keys the
    // list nor recreates its rows; a fact this page added keeps its own key until the server names it.
    const previous = new Map(this.draftState().filter((fact) => fact.id !== '').map((fact) => [fact.id, fact.clientKey]));
    const facts = sheet?.facts ?? [];
    this.draftState.set(facts.map((fact) => ({ ...fact, clientKey: previous.get(fact.id) ?? newClientKey() })));
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
    // F8: the edit is what fixes the field the refused save named, so that pointer (and its banner) retires here.
    this.retireInvalidContentError();
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

  /**
   * A failure writes state only while its answer still belongs to the episode this instance shows: a late answer from
   * another episode (or one that arrives after a reset) is returned to its caller without touching any surface.
   */
  private failRow(cause: unknown, episodeId: string, generation: number): AppError {
    const error = this.toError(cause);
    if (!this.current(generation) || this.id !== episodeId) return error;
    // Row actions have no inline field: the banner is the one place that can explain a refused remove/retry.
    this.panelErrorState.set(error);
    this.captureRateLimit(error);
    return error;
  }

  private failPanel(error: AppError, episodeId: string, generation: number): AppError {
    if (!this.current(generation) || this.id !== episodeId) return error;
    this.panelErrorState.set(error);
    this.captureRateLimit(error);
    return error;
  }

  private failWrite(cause: unknown, etag: string, episodeId: string, generation: number, source: ConflictSource): AppError {
    const error = this.toError(cause);
    if (!this.current(generation) || this.id !== episodeId) return error;
    // 412 is the divergence the ConflictDialog resolves: it offers Reload latest / Save mine as new version, and only its
    // second button re-PUTs. The refused ETag and the write that earned it are what the dialog needs (F5).
    if (error.status === 412) {
      this.conflictState.set({ etag });
      this.conflictSourceState.set(source);
      return error;
    }
    // 428 says this client sent no If-Match at all: a bug of ours, not a divergence, and there is nothing for the user to
    // choose between — so it is the banner's business (`if_match_required`, the catalogue's own code), never the dialog.
    if (error.status === 428) {
      this.panelErrorState.set(error);
      return error;
    }
    return this.failPanel(error, episodeId, generation);
  }

  private clearConflict(): void {
    this.conflictState.set(null);
    this.conflictSourceState.set(null);
  }

  /**
   * A 429 is a wait the server imposed. `page` holds every write (generate/save/approve/remove/retry); `form` holds the
   * add form alone, so a url the server refuses for the day never locks the sheet path. `Retry-After: 0` and a missing
   * header are not waits: the state stays `null` rather than holding a control for zero seconds (README D9).
   */
  private captureRateLimit(error: AppError | null, scope: 'page' | 'form' = 'page'): void {
    const state = scope === 'form' ? this.formRateLimitState : this.rateLimitState;
    const seconds = error?.retryAfterSec;
    state.set(seconds !== undefined && seconds > 0 ? seconds : null);
  }

  /** The dirty comparison without the 150 ms trailing window: a decision never waits for the debounced flag (F7). */
  private dirtyNow(): boolean {
    return serializeFacts(this.draftState()) !== this.baselineJson();
  }

  /**
   * F8: a refusal that names a fact's field (`fact_sheet_invalid_content`) is retired by the edit that fixes it — the
   * draft the pointers described is not the draft on screen any more, so the banner (and with it every per-fact pointer
   * it carried, the panel reads them from this one error) goes; the next save reports afresh what is still wrong.
   */
  private retireInvalidContentError(): void {
    if (this.panelErrorState()?.code === 'fact_sheet_invalid_content') this.panelErrorState.set(null);
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
