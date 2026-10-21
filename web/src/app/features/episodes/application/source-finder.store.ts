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
 * Reads: `watch` (the initial load when the flag is already known and the coalesced realtime refetch), the visibility
 * trigger (the initial load when the flag arrives later, once per episode) and this store's own row actions are the
 * only triggers — no polling, no timer of this store's own (the 250 ms window belongs to `RealtimeService`). Every
 * answer is fenced by the request generation of the episode it belongs to and by the read token that names the newest
 * read, so a late answer of an episode or a session the page has left is dropped whole.
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
  /** The query the open intent was minted for: a different one is a different intent (and so a different key). */
  private findQuery: string | null = null;
  private profileReloaded = false;
  /** The episode this store has already started its first read for: the trigger below reads once per episode. */
  private initialReadEpisode: string | null = null;
  /**
   * Names the newest candidates read at any moment. Two reads can overlap across an episode switch (`watch` starts the
   * new episode's read while the old one is still open), so only the newest token may apply rows or end the flags: an
   * older read's answer and an older read's `finally` belong to the episode the page has left.
   */
  private readToken = 0;
  /** A candidates read is in flight: a request that lands inside it queues one trailing read (see `requestCandidates`). */
  private candidatesInFlight = false;
  /** The request asked for while a read was open; the read's own end runs it once, and only if still current. */
  private refetchQueued: { readonly userAction: boolean; readonly silent: boolean } | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.requestGeneration += 1;
      this.subscription?.unsubscribe();
      this.subscription = null;
      this.findIntent = null;
    });
    effect(() => {
      // The flag arrives with `/me`, which can resolve after the page armed this store, so the first read is owed the
      // moment the block becomes visible. Reading `visible()` here is what arms this trigger; while it is false nothing
      // goes out at all, and the per-episode marker is what keeps this from running twice for one episode.
      if (!this.visible()) return;
      const episodeId = this.episode;
      if (episodeId === null) return;
      untracked(() => this.ensureInitialRead(episodeId));
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
   * re-evaluates. Events and every (re)connect resync arrive coalesced from `RealtimeService` (one call per 250 ms per
   * episode), and a request that lands while this store's own read is in flight queues one trailing read rather than
   * stacking a second GET (see `requestCandidates`).
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
    this.findQuery = null;
    this.newFindState.set(false);
    // Nothing of the episode left over may gate this one: a find that was in flight when the page moved is stale, and
    // its own `finally` is generation-fenced, so these flags are the block's, not the old request's.
    this.findingState.set(false);
    this.loadingState.set(false);
    this.candidatesInFlight = false;
    this.refetchQueued = null;
    // One first read is owed per episode.
    this.initialReadEpisode = null;
    if (this.visible()) this.ensureInitialRead(episodeId);
    this.subscription = this.realtime.subscribeEpisode(episodeId, () => {
      if (this.destroyed || this.requestGeneration !== generation || this.episode !== episodeId) return;
      // A background read is silent: its failure is not the page's error (the next event or the owner's own click
      // reports afresh).
      this.requestCandidates(episodeId, false, true);
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
    // An open intent describes the search it was minted for: a retry of that search replays the same key, while a
    // different query is a different intent, so the kept key is settled here instead of carrying a body it never had
    // (the server answers 422 `idempotency_key_reused` for a reused key whose body changed).
    if (this.findIntent !== null && this.findQuery !== sent) {
      this.findIntent.settle();
      this.findIntent = null;
    }
    if (this.findIntent === null) {
      this.findIntent = this.keys.begin('source.find');
      this.findQuery = sent;
    }
    const intent = this.findIntent;
    const generation = this.requestGeneration;
    try {
      await this.repo.find(episodeId, sent, intent);
      intent.settle();
      if (this.findIntent === intent) {
        this.findIntent = null;
        this.findQuery = null;
      }
      if (!this.current(generation) || this.episode !== episodeId) return this.refused();
      // A landed search ends the wait a previous 429 announced.
      this.capturePanelRetry(null);
      // The 202 is the server's answer, not yet the episode's state: `running` only turns true once the page's own data
      // is re-read, so the click is held until that read is done — otherwise the button would be live again while the
      // block still looks idle, and a second click would start a second run. The re-read is this click's bookkeeping:
      // a failure of it is not a search failure, so it never becomes the block's error.
      try {
        await this.research.refresh(episodeId);
      } catch {
        // The search itself landed; the block's run state catches up on the next read or event.
      }
      return { ok: true, value: undefined };
    } catch (cause) {
      const error = this.toError(cause);
      // A transport failure (status 0) is the one retryable answer: the key stays open so the next click replays it.
      if (error.status !== 0) {
        intent.settle();
        if (this.findIntent === intent) {
          this.findIntent = null;
          this.findQuery = null;
        }
      }
      // D14/O1: a flag that went off is not a failure — the block hides, the profile is re-read, nothing is shown. Only
      // the episode this store still shows may hide it: a late answer of the episode the page has left changes nothing.
      if (error.code === FEATURE_DISABLED) {
        if (this.current(generation) && this.episode === episodeId) this.hide();
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
      // The row's new state comes from the server, so it is read after the page's own data — through the one entry point,
      // which queues it behind a candidates read that is still open instead of racing it.
      this.requestCandidates(episodeId, false, true);
      return { ok: true, value: undefined };
    } catch (cause) {
      return { ok: false, error: this.failRowAction(cause, episodeId, generation, candidateId) };
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
      return { ok: false, error: this.failRowAction(cause, episodeId, generation, candidateId) };
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
    this.readToken += 1;
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
    this.findQuery = null;
    this.profileReloaded = false;
    this.initialReadEpisode = null;
    this.candidatesInFlight = false;
    this.refetchQueued = null;
  }

  /**
   * The one way a candidates read is asked for. While this episode's read is open the request is queued instead of
   * stacked — the newest request decides how the trailing read runs, so nothing races two answers into one list — and
   * while the block is hidden (or the flag is off) no request exists at all.
   */
  private requestCandidates(episodeId: string, userAction: boolean, silent: boolean): void {
    if (!this.visible() || this.destroyed || this.episode !== episodeId) return;
    if (this.candidatesInFlight) {
      this.refetchQueued = { userAction, silent };
      return;
    }
    void this.readCandidates(episodeId, userAction, silent);
  }

  /** The flip of `visible()` to true owes this episode one read; the marker is what keeps the trigger from looping. */
  private ensureInitialRead(episodeId: string): void {
    if (this.destroyed || this.episode !== episodeId || this.initialReadEpisode === episodeId) return;
    this.initialReadEpisode = episodeId;
    this.requestCandidates(episodeId, true, false);
  }

  /**
   * `GET …/sources/candidates`. Never called while the block is hidden or the flag is off (so a disabled feature
   * costs no request at all).
   */
  private async readCandidates(episodeId: string, userAction: boolean, silent: boolean): Promise<boolean> {
    if (!this.visible() || this.destroyed) return false;
    const token = ++this.readToken;
    this.candidatesInFlight = true;
    const generation = this.requestGeneration;
    if (!silent) {
      this.loadingState.set(true);
      this.errorState.set(null);
    }
    try {
      const rows = await this.repo.candidates(episodeId, { userAction });
      // Only the newest read may apply rows: an older answer (an episode the page has left, a read this one replaced)
      // is at best as new as what the list already shows, and a just-added row must never revert to its older state.
      if (token !== this.readToken || !this.current(generation) || this.episode !== episodeId) return false;
      this.candidatesState.set(rows);
      return true;
    } catch (cause) {
      const error = this.toError(cause);
      if (token !== this.readToken || !this.current(generation) || this.episode !== episodeId) return false;
      if (error.code === FEATURE_DISABLED) {
        this.hide();
        return false;
      }
      if (!silent) this.errorState.set(error);
      return false;
    } finally {
      // The flags belong to the newest read only: an older read ending late must not release a lock or consume a queue
      // that the current read owns (that is how a queued refetch was lost before).
      if (token === this.readToken) {
        this.candidatesInFlight = false;
        if (this.current(generation)) this.loadingState.set(false);
        const queued = this.refetchQueued;
        this.refetchQueued = null;
        if (queued !== null && this.current(generation) && this.episode === episodeId) {
          this.requestCandidates(episodeId, queued.userAction, queued.silent);
        }
      }
    }
  }

  /**
   * One refused row action. The answer only touches a surface while it still belongs to the episode this store shows:
   * an unknown or foreign row is the server's business (the list is re-read, no error is raised), and anything else
   * becomes that row's own error, leaving every other row live.
   */
  private failRowAction(cause: unknown, episodeId: string, generation: number, candidateId: string): AppError {
    const error = this.toError(cause);
    if (!this.current(generation) || this.episode !== episodeId) return error;
    if (error.code === FEATURE_DISABLED) {
      this.hide();
      return error;
    }
    // Already added or dismissed (in another tab): the row refreshes to the server's state instead of a second error.
    if (error.code === ALREADY_HANDLED) {
      this.requestCandidates(episodeId, false, true);
      return error;
    }
    this.setRowError(candidateId, error);
    this.captureRowRetry(error);
    // A row the server no longer has (another episode's candidate, a stale id): the list is the answer, and the row
    // still says what happened until the read drops it.
    if (error.status === 404) this.requestCandidates(episodeId, false, true);
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
