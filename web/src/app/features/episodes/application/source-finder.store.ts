import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { AppErrorMapper, type AppError, type Result } from '@core/errors';
import { IdempotencyKeys, type IdempotencyIntent } from '@core/http';
import { RealtimeService, type RealtimeSubscription } from '@core/realtime';
import { MeRepository, SessionStore, type MeSummary } from '@core/session';
import { FIND_QUERY_MAX_CHARS, type SourceCandidate } from '../domain/source-candidate.model';
import { SourceFinderRepository } from '../domain/source-finder.repository';
import { isAppError } from './episode-detail.store';
import { ResearchStore } from './research.store';

/** The flag that gates the block (README D14/O1). */
const SOURCE_FINDER_FEATURE = 'source_finder';
/** The run kind the finder starts: the block's run state is the episode's own `generation` (02.7 `plan.md` §5). */
const FIND_SOURCES_KIND = 'find_sources';
/** The `FindSourcesGenerationDto.status` values that mean the run is on its way (02.7.2-T2 `plan.md` §5). */
const RUNNING_STATUS: Record<string, true> = { queued: true, running: true };
/** The one 404 that hides the block instead of raising an error (D14/O1). */
const FEATURE_DISABLED = 'feature_disabled';
/** The row is already handled (added or dismissed elsewhere): the list re-reads, no error is shown for the row. */
const ALREADY_HANDLED = 'source_candidate_not_proposed';

/**
 * Page-scoped state of the find-sources block (10.5.8-T1), provided by the research route. The run it starts is
 * episode-level and its state lives in `EpisodeDetail.generation`, which `ResearchStore` owns and reads: this store
 * never keeps a run of its own, so a reloaded page still shows "searching" from the server's own state.
 *
 * Reads: `watch` (the initial load and the coalesced realtime refetch) is the only trigger — no polling, no timer of
 * this store's own (the 250 ms window belongs to `RealtimeService`). Every answer is fenced by the request generation
 * of the episode it belongs to, so a late answer of an episode or a session the page has left is dropped whole.
 */
@Injectable()
export class SourceFinderStore {
  private readonly repo = inject(SourceFinderRepository);
  /** The episode's own state: `generation()` names the live run, `refresh()` re-reads sources and limits after an add. */
  private readonly research = inject(ResearchStore);
  private readonly session = inject(SessionStore);
  private readonly realtime = inject(RealtimeService);
  private readonly keys = inject(IdempotencyKeys);
  private readonly mapper = inject(AppErrorMapper);
  /**
   * The profile port, bound by `provideShell()` (10.2.6-T1). Optional so the block is usable outside the shell (the
   * host's own specs): without a profile to re-read, the `/me` refetch below is skipped instead of crashing.
   */
  private readonly me = inject(MeRepository, { optional: true });
  private readonly ownerGeneration = this.session.generation();

  private readonly visibleState = signal(false);
  private readonly candidatesState = signal<readonly SourceCandidate[]>([]);
  private readonly findingState = signal(false);
  private readonly loadingState = signal(false);
  private readonly errorState = signal<AppError | null>(null);
  private readonly rowErrorsState = signal<ReadonlyMap<string, AppError>>(new Map());
  private readonly busyIdState = signal<string | null>(null);
  private readonly retryAfterState = signal<number | null>(null);
  private readonly rowRetryAfterState = signal<number | null>(null);
  /** False once this instance learned that the flag is off server-side (a 404 `feature_disabled`). */
  private readonly hiddenState = signal(false);
  /** True from the click that starts a search: the previous run's failure is not what the block reports any more. */
  private readonly newFindState = signal(false);

  /**
   * The block exists only with the flag (README D14/O1) and never comes back after the server said it is off: with the
   * flag off — or `features` missing entirely, which `hasFeature` reads as off — no call of any kind goes out.
   */
  readonly visible = computed(() => this.session.hasFeature(SOURCE_FINDER_FEATURE) && !this.hiddenState());
  /** Server order, statuses included: a `dismissed` row stays in the list (the panel decides how to render it). */
  readonly candidates = this.candidatesState.asReadonly();
  /** The run state, straight from the episode: `find_sources` queued or running (so it survives a reload). */
  readonly running = computed(() => {
    const run = this.research.generation();
    return run !== null && run.kind === FIND_SOURCES_KIND && RUNNING_STATUS[run.status] === true;
  });
  /** A `find_sources` run that failed, until the owner starts a new search (or the run state moves on). */
  readonly failedRun = computed(() => {
    const run = this.research.generation();
    return run !== null && run.kind === FIND_SOURCES_KIND && run.status === 'failed' && !this.newFindState();
  });
  /** This store's own `POST …/sources/find` is in flight; a second click while it is true sends no request. */
  readonly finding = this.findingState.asReadonly();
  /** A candidates read is in flight. */
  readonly loading = this.loadingState.asReadonly();
  /** Panel-level failure: the search itself, `find_sources_in_progress`, `spend_cap_reached`, or a failed read. */
  readonly error = this.errorState.asReadonly();
  /** Per-candidate failure of an add/dismiss, keyed by candidate id: one refused row leaves the others untouched. */
  readonly rowErrors = this.rowErrorsState.asReadonly();
  /** The candidate id with an add or a dismiss in flight; only one row acts at a time. */
  readonly busyId = this.busyIdState.asReadonly();
  /** The wait a 429 announced for the search (`Retry-After`, integer seconds; `null` when there is none). */
  readonly retryAfterSec = this.retryAfterState.asReadonly();
  /** The wait a 429 announced for a row action (`source_fetch_limit_reached`). */
  readonly rowRetryAfterSec = this.rowRetryAfterState.asReadonly();
  readonly canFind = computed(() =>
    this.visible() && !this.running() && !this.findingState() && this.retryAfterState() === null && !this.destroyed);

  private destroyed = false;
  private episode: string | null = null;
  private requestGeneration = 0;
  private subscription: RealtimeSubscription | null = null;
  private findIntent: IdempotencyIntent | null = null;
  private profileReloaded = false;
  /** A candidates read is in flight: an event that lands inside it queues one trailing read (see the subscription). */
  private candidatesInFlight = false;
  /** An event asked for a read while one was in flight; the read's own end runs it once, and only if still current. */
  private refetchQueued = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.requestGeneration += 1;
      this.subscription?.unsubscribe();
      this.subscription = null;
      this.findIntent = null;
    });
    effect(() => {
      // A run of this episode that is not (or no longer) a failure is this click's own outcome, so the message of the
      // run before it retires here.
      const run = this.research.generation();
      if (run !== null && run.kind === FIND_SOURCES_KIND && run.status !== 'failed') this.newFindState.set(false);
      // A new session owns nothing of the old one (the same rule `ResearchStore` applies).
      if (this.session.generation() !== this.ownerGeneration) untracked(() => this.reset());
    });
  }

  /**
   * Arms the block for one episode: the first candidates read (only when the block is visible) and the realtime
   * subscription that refetches them. Idempotent per episode, so the page's own effect may call it as often as it
   * re-evaluates. Events and every (re)connect resync arrive coalesced from `RealtimeService` (one call per 250 ms
   * per episode), and a call that lands while this store's own read is in flight is dropped rather than stacked.
   */
  watch(episodeId: string): void {
    if (episodeId === '' || this.destroyed) return;
    if (this.episode === episodeId && this.subscription !== null) return;
    this.subscription?.unsubscribe();
    this.subscription = null;
    // Everything in flight belongs to the episode this store is leaving: its answers are stale from here on.
    this.requestGeneration += 1;
    const generation = this.requestGeneration;
    this.episode = episodeId;
    this.candidatesState.set([]);
    this.errorState.set(null);
    this.rowErrorsState.set(new Map());
    this.busyIdState.set(null);
    this.retryAfterState.set(null);
    this.rowRetryAfterState.set(null);
    this.findIntent = null;
    this.newFindState.set(false);
    // Nothing of the episode left over may gate this one: a find that was in flight when the page moved is stale, and
    // its own `finally` is generation-fenced, so these flags are the block's, not the old request's.
    this.findingState.set(false);
    this.loadingState.set(false);
    this.candidatesInFlight = false;
    this.refetchQueued = false;
    if (this.visible()) void this.readCandidates(episodeId, true, false);
    this.subscription = this.realtime.subscribeEpisode(episodeId, () => {
      if (this.destroyed || this.requestGeneration !== generation || this.episode !== episodeId) return;
      // A background read is silent: its failure is not the page's error (the next event or the owner's own click
      // reports afresh). An event that lands while this episode's own read is still open is never lost: it asks for
      // exactly one trailing read, which the read's own end runs, so a candidate the server committed in the meantime
      // is picked up and a burst inside one read still costs one extra GET.
      if (this.candidatesInFlight) {
        this.refetchQueued = true;
        return;
      }
      void this.readCandidates(episodeId, false, true);
    });
  }

  /**
   * `POST …/sources/find` (202). One intent per click: a transport failure keeps the key so a retry replays the same
   * intent, and every real answer settles it — a 422 `idempotency_key_reused` included, so the next click mints a
   * fresh one. The query is sent only when the caller has one and it fits the server's own 300-char validator.
   */
  async find(episodeId: string, query?: string): Promise<Result<void>> {
    if (!this.canFind()) return this.refused();
    const trimmed = query?.trim() ?? '';
    const sent = trimmed !== '' && trimmed.length <= FIND_QUERY_MAX_CHARS ? trimmed : null;
    this.findingState.set(true);
    this.errorState.set(null);
    this.newFindState.set(true);
    if (this.findIntent === null) this.findIntent = this.keys.begin('source.find');
    const intent = this.findIntent;
    const generation = this.requestGeneration;
    try {
      await this.repo.find(episodeId, sent, intent);
      intent.settle();
      this.findIntent = null;
      if (!this.current(generation) || this.episode !== episodeId) return this.refused();
      // A landed search ends the wait a previous 429 announced.
      this.capturePanelRetry(null);
      return { ok: true, value: undefined };
    } catch (cause) {
      const error = this.toError(cause);
      // A transport failure (status 0) is the one retryable answer: the key stays open so the next click replays it.
      if (error.status !== 0) {
        intent.settle();
        this.findIntent = null;
      }
      // D14/O1: a flag that went off is not a failure — the block hides, the profile is re-read, nothing is shown.
      if (error.code === FEATURE_DISABLED) {
        this.hide();
        return { ok: false, error };
      }
      if (this.current(generation) && this.episode === episodeId) {
        this.errorState.set(error);
        this.capturePanelRetry(error);
      }
      return { ok: false, error };
    } finally {
      if (this.current(generation)) this.findingState.set(false);
    }
  }

  /**
   * `POST …/candidates/{id}/add` (201): one request per click, and only one row acts at a time. The 201 is the truth
   * about the source list, so the page re-reads its own data (10.5.5-T1 owns the list and its counters) and this block
   * re-reads the candidates, whose row now carries the status the server stored.
   */
  async add(episodeId: string, candidateId: string): Promise<Result<void>> {
    if (!this.rowActionAvailable()) return this.refused();
    const generation = this.requestGeneration;
    this.busyIdState.set(candidateId);
    this.clearRowError(candidateId);
    try {
      await this.repo.add(episodeId, candidateId);
      if (!this.current(generation) || this.episode !== episodeId) return this.refused();
      await this.research.refresh(episodeId);
      await this.readCandidates(episodeId, false, true);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: await this.failRowAction(cause, episodeId, generation, candidateId) };
    } finally {
      if (this.current(generation)) this.busyIdState.set(null);
    }
  }

  /**
   * `POST …/candidates/{id}/dismiss` (204). The answer carries nothing, so the row's own status is updated locally:
   * the episode's source list is untouched by a dismissal.
   */
  async dismiss(episodeId: string, candidateId: string): Promise<Result<void>> {
    if (!this.rowActionAvailable()) return this.refused();
    const generation = this.requestGeneration;
    this.busyIdState.set(candidateId);
    this.clearRowError(candidateId);
    try {
      await this.repo.dismiss(episodeId, candidateId);
      if (!this.current(generation) || this.episode !== episodeId) return this.refused();
      this.candidatesState.update((rows) =>
        rows.map((row) => (row.id === candidateId ? { ...row, status: 'dismissed' as const } : row)));
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: await this.failRowAction(cause, episodeId, generation, candidateId) };
    } finally {
      if (this.current(generation)) this.busyIdState.set(null);
    }
  }

  clearError(): void {
    this.errorState.set(null);
  }

  /** The panel's countdown for the search elapsed: the wait is over and the search is offered again. */
  clearRetry(): void {
    this.retryAfterState.set(null);
  }

  /**
   * Drops one row's error. `source_fetch_limit_reached` is the row-level 429 and its countdown belongs to that error,
   * so the elapsed countdown releases the wait with it (D9: the action is held only until the countdown reaches 0).
   */
  clearRowError(candidateId: string): void {
    const error = this.rowErrorsState().get(candidateId);
    if (error === undefined) return;
    const next = new Map(this.rowErrorsState());
    next.delete(candidateId);
    this.rowErrorsState.set(next);
    if (error.retryAfterSec !== undefined) this.rowRetryAfterState.set(null);
  }

  /** Drops every read, error and intent of this instance (a leave, a logout, a session end). */
  reset(): void {
    this.requestGeneration += 1;
    this.episode = null;
    this.candidatesState.set([]);
    this.errorState.set(null);
    this.rowErrorsState.set(new Map());
    this.busyIdState.set(null);
    this.retryAfterState.set(null);
    this.rowRetryAfterState.set(null);
    this.findingState.set(false);
    this.loadingState.set(false);
    this.newFindState.set(false);
    this.hiddenState.set(false);
    this.findIntent = null;
    this.profileReloaded = false;
  }

  /**
   * `GET …/sources/candidates`. Never called while the block is hidden or the flag is off (so a disabled feature
   * costs no request at all).
   */
  private async readCandidates(episodeId: string, userAction: boolean, silent: boolean): Promise<boolean> {
    if (!this.visible() || this.destroyed) return false;
    this.candidatesInFlight = true;
    const generation = this.requestGeneration;
    if (!silent) {
      this.loadingState.set(true);
      this.errorState.set(null);
    }
    try {
      const rows = await this.repo.candidates(episodeId, { userAction });
      if (!this.current(generation) || this.episode !== episodeId) return false;
      this.candidatesState.set(rows);
      return true;
    } catch (cause) {
      const error = this.toError(cause);
      if (!this.current(generation) || this.episode !== episodeId) return false;
      if (error.code === FEATURE_DISABLED) {
        this.hide();
        return false;
      }
      if (!silent) this.errorState.set(error);
      return false;
    } finally {
      this.candidatesInFlight = false;
      if (this.current(generation)) this.loadingState.set(false);
      // The trailing read an event asked for while this one was open. It is generation-fenced (an episode switch, a
      // reset or a new session discards it) and consumed once, so a burst never stacks GETs and nothing polls.
      if (this.refetchQueued) {
        this.refetchQueued = false;
        if (this.current(generation) && this.episode === episodeId) void this.readCandidates(episodeId, false, true);
      }
    }
  }

  /**
   * One refused row action. The answer only touches a surface while it still belongs to the episode this store shows:
   * an unknown or foreign row is the server's business (the list is re-read, no error is raised), and anything else
   * becomes that row's own error, leaving every other row live.
   */
  private async failRowAction(cause: unknown, episodeId: string, generation: number, candidateId: string): Promise<AppError> {
    const error = this.toError(cause);
    if (!this.current(generation) || this.episode !== episodeId) return error;
    if (error.code === FEATURE_DISABLED) {
      this.hide();
      return error;
    }
    // Already added or dismissed (in another tab): the row refreshes to the server's state instead of a second error.
    if (error.code === ALREADY_HANDLED) {
      await this.readCandidates(episodeId, false, true);
      return error;
    }
    this.setRowError(candidateId, error);
    this.captureRowRetry(error);
    // A row the server no longer has (another episode's candidate, a stale id): the list is the answer, and the row
    // still says what happened until the read drops it.
    if (error.status === 404) await this.readCandidates(episodeId, false, true);
    return error;
  }

  private rowActionAvailable(): boolean {
    return this.visible() && !this.destroyed && this.busyIdState() === null && this.rowRetryAfterState() === null;
  }

  private setRowError(candidateId: string, error: AppError): void {
    const next = new Map(this.rowErrorsState());
    next.set(candidateId, error);
    this.rowErrorsState.set(next);
  }

  /** 404 `feature_disabled` (D14/O1): the flag went off mid-session — hide, re-read `/me`, report nothing. */
  private hide(): void {
    this.hiddenState.set(true);
    this.reloadProfile();
  }

  /**
   * One `/me` refetch per instance: `visible` reads the profile, so re-reading it is what stops the block from coming
   * back for a flag the server has turned off. `MeRepository` is bound by the shell, so outside it (a spec) there is
   * no profile to re-read; a failed refetch changes nothing (the block is hidden either way) and is never surfaced.
   */
  private reloadProfile(): void {
    if (this.profileReloaded) return;
    this.profileReloaded = true;
    const me = this.me;
    if (me === null) return;
    const generation = this.session.generation();
    void me.get().then(
      (summary: MeSummary) => {
        if (!this.destroyed && this.session.generation() === generation) this.session.setMe(summary);
      },
      () => undefined,
    );
  }

  /** README D9: a `Retry-After: 0` and a missing header are not waits (a control is never held for zero seconds). */
  private capturePanelRetry(error: AppError | null): void {
    const seconds = error?.retryAfterSec;
    this.retryAfterState.set(seconds !== undefined && seconds > 0 ? seconds : null);
  }

  private captureRowRetry(error: AppError | null): void {
    const seconds = error?.retryAfterSec;
    this.rowRetryAfterState.set(seconds !== undefined && seconds > 0 ? seconds : null);
  }

  /** A refused call is a no-op: it reports the live error of its own surface, or the mapper's plain `unknown_error`
   * marker — never a fabricated HTTP status. */
  private refused(): Result<void> {
    return { ok: false, error: this.errorState() ?? this.mapper.from(null) };
  }

  private toError(cause: unknown): AppError {
    return isAppError(cause) ? cause : this.mapper.from(cause);
  }

  private current(generation: number): boolean {
    return !this.destroyed && generation === this.requestGeneration && this.session.generation() === this.ownerGeneration;
  }
}
