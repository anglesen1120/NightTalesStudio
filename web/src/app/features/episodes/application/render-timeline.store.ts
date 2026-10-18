import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { RealtimeService } from '@core/realtime';
import type { RealtimeSubscription } from '@core/realtime';
import type { EpisodeDetail } from '../domain/episode.model';
import type { RenderFailure, RenderJob, RenderStatus, RenderTimelineStep, SubStage } from '../domain/render.model';
import { EpisodeDetailStore } from './episode-detail.store';
import { imageDetailCode } from './render-failure';
import { EMPTY_JOB, clampPercent, isGuaranteedStage, isSubStage, renderTimeline } from './render-stage';

/** The statuses a job is still being worked on in (WD2): the only ones that arm the elapsed-time clock. */
const ACTIVE_STATUSES: readonly RenderStatus[] = ['queued', 'submitted', 'running', 'ingesting'];

/** WB2: a `queued` job with no `submitted_at` for this long is "waiting for a free studio slot", not an error. */
export const WAITING_FOR_SLOT_SEC = 30;

/** The one-second clock of the elapsed time (D13/D17): the tick sets `now` and nothing else — no HTTP, no refetch. */
const TICK_MS = 1000;

/**
 * Elapsed whole seconds between a start instant and `now`. `submittedAt` is an ISO string and the client-only
 * `queuedSince` is epoch ms; an unparsable date, a start in the future (clock skew) or a lapsed start all yield `0`
 * instead of a negative or `NaN` duration. `finishedAt` is never a baseline: the clock stops, it does not restart.
 */
export function elapsedSeconds(since: string | number | null, now: number): number {
  if (since === null) return 0;
  const start = typeof since === 'number' ? since : Date.parse(since);
  if (!Number.isFinite(start)) return 0;
  const seconds = Math.floor((now - start) / 1000);
  return seconds > 0 ? seconds : 0;
}

/**
 * The render timeline of `/episodes/:id` (10.6.1-T1), provided by the episode page in its component `providers`,
 * beside `EpisodeDetailStore` (so the `DestroyRef` below is the page's own injector and the state dies with it).
 *
 * Ownership (D11/WR4): `EpisodeDetailStore` is the only reader of `GET /episodes/{id}` and `RealtimeService` the only
 * owner of the socket and of the progress map — this store injects both, calls neither repository nor `HttpClient`, and
 * issues one read only when asked (`refresh()`), always without `USER_ACTION` (WR11/D7).
 *
 * The job it shows is the detail's (`status`, `submittedAt`, `errorCode`, `images` — the fields only the API knows)
 * merged with a live `RenderProgress` for the same episode (`stage`/`percent`) whenever that entry names this job.
 * Nothing of the wire is rendered as text: `render-stage.ts` and `render-failure.ts` turn every value into a key.
 *
 * Known residual (not closable here): the progress map holds one entry per episode and keeps no job id or timestamp
 * (`RenderProgress` is `{stage, percent}` — 10.2.4-T1), so an entry left over from a *previous* job of the same
 * episode is indistinguishable from a fresh event of the current one. Within this task that state cannot be reached
 * (an episode enters the `rendering` branch with no earlier job), and 10.6.2-T1's re-make is corrected by the detail
 * refetch that follows it; the fix belongs in 10.2.4-T1 — either put the event's `ids.renderJobId` into the map entry
 * or drop the episode's entry when a new `renderJobId` starts. Distrusting every pre-existing entry here is not an
 * option: a progress event that arrives before the detail is loaded must still merge (the task's failure-handling row).
 *
 * The 1 Hz clock is local rather than `core/state/visible-poll` (D11/WR4): that helper exists to *poll* — it needs
 * `ActivityTracker`, so an idle owner would watch a frozen elapsed time, and it runs its first tick immediately and
 * keeps going until `stop()`, while this clock must follow `active()` (armed while the job works, gone at the first
 * terminal status) and only stamp the current time.
 */
@Injectable()
export class RenderTimelineStore {
  private readonly detail = inject(EpisodeDetailStore);
  private readonly realtime = inject(RealtimeService);
  private readonly document = inject(DOCUMENT);

  /** The episode the page armed this store for; a signal, so a `:id` reuse restarts everything derived from it. */
  private readonly episodeId = signal<string | null>(null);
  /** Whether the tab is visible: the tick runs only while it is (D7/D13), re-read on every `visibilitychange`. */
  private readonly visibleState = signal(this.document.visibilityState === 'visible');
  /** The client-only elapsed baseline (WB2): stamped on the first observation of `queued`, never persisted. */
  private readonly queuedSinceState = signal<number | null>(null);
  private readonly nowState = signal(Date.now());

  /** The 1 Hz clock the elapsed time is derived from. Public because the view formats the same instant. */
  readonly now = this.nowState.asReadonly();
  /** `true` while the document is visible: the tick is stopped while it is not. */
  readonly visible = this.visibleState.asReadonly();
  /** The client-only wait baseline, or `null` once `submittedAt` exists or the job is over. */
  readonly queuedSince = this.queuedSinceState.asReadonly();

  /** The detail of the armed episode; a detail of another episode (a route move) is not this store's. */
  private readonly current = computed<EpisodeDetail | null>(() => {
    const episode = this.detail.episode();
    return episode !== null && episode.id === this.episodeId() ? episode : null;
  });

  /**
   * This episode's live entry, or `undefined`. Reading the entry of the map — not the map — is what keeps `job` from
   * recomputing on another episode's progress: `RealtimeService.queueProgress` replaces the whole map on every flush,
   * but the entry of an episode nobody pushed to is the same object across those replacements, so a `computed` that
   * reads it does not change.
   */
  private readonly live = computed(() => this.realtime.progress().get(this.episodeId() ?? ''));

  /**
   * The job as shown (F5/F6/WD2). The detail wins for `submittedAt`, `errorCode` and `images`; a live `RenderProgress`
   * for this episode wins for `stage`/`percent` only, and only while its `renderJobId` (when the entry carries one —
   * the current payload is `{stage, percent}`, 10.2.4-T1) is this job's: an entry left over from a previous job of the
   * same episode is ignored. A live stage this client does not know never enters `stage`.
   *
   * The status is the detail's, with one promotion: a live stage *ahead* of the detail's `queued`/`submitted` proves
   * the job started, so it is shown as `running` — otherwise a 42 % bar would sit under an active `queued` step until
   * the next detail read. Every other status stays the detail's (`ingesting` and the terminal ones are never regressed),
   * and the promoted job stops being "waiting for a slot" (that state is `queued` only).
   */
  readonly job = computed<RenderJob | null>(() => {
    const episode = this.current();
    const detailJob = episode?.renderJob ?? null;
    if (episode === null || detailJob === null) return null;
    const live = this.live();
    if (live === undefined) return detailJob;
    // `renderJobId` is not part of `RenderProgress` (`{stage, percent}` — 10.2.4-T1), so it is read as an unlisted
    // property: an entry that does name a job must name *this* job, and one that does not is this episode's progress.
    if ('renderJobId' in live && typeof live.renderJobId === 'string' && live.renderJobId !== detailJob.renderJobId) return detailJob;
    const liveStage = isGuaranteedStage(live.stage) || isSubStage(live.stage) ? live.stage : null;
    const queuedDetail = detailJob.status === 'queued' || detailJob.status === 'submitted';
    return {
      ...detailJob,
      status: queuedDetail && liveStage !== null && liveStage !== 'queued' ? 'running' : detailJob.status,
      stage: liveStage ?? detailJob.stage,
      percent: clampPercent(live.percent),
    };
  });

  /** The three steps, from the single stage mapping: with no job known they render three `todo` steps. */
  readonly steps = computed<readonly RenderTimelineStep[]>(() => renderTimeline(this.job() ?? EMPTY_JOB));

  /** The bar's value, clamped: a percent from the wire or from a live event never leaves 0…100. */
  readonly percent = computed(() => clampPercent(this.job()?.percent));

  /**
   * The node sub-step under the *active* stage, or `null`. It comes from the `running` step, so it disappears when the
   * detail moves past `running`: `''`, `'queued '`, `'RENDERING'` and `'v2'` are unknown (F3), and a detail that says
   * `ingesting` shows none even when the last live event said `assembling` (the detail owns the status).
   */
  readonly subStepKey = computed<SubStage | null>(() => this.steps().find((step) => step.key === 'running')?.subStepKey ?? null);

  /**
   * The elapsed time shown: from `submittedAt` when the job reached a node, otherwise from the client-only
   * `queuedSince` — `submitted_at` stays `null` for the whole wait (09.6.1-T1 sets it at `MarkSubmitted`), so without
   * that stamp the waiting state could never reach its 30 s threshold.
   */
  readonly elapsedSec = computed(() => elapsedSeconds(this.job()?.submittedAt ?? this.queuedSinceState(), this.nowState()));

  /** `queued` without a `submitted_at` past 30 s: the studio has no free slot yet — a state, never an error (WB2/F4). */
  readonly waitingForSlot = computed(() => {
    const job = this.job();
    return job !== null && job.status === 'queued' && job.submittedAt === null && this.elapsedSec() >= WAITING_FOR_SLOT_SEC;
  });

  /**
   * The failure the panel renders, or `null`. A `failed` job always opens it, even when the wire carried no
   * `errorCode`: the panel's empty code resolves to the generic title and no action, which is still a failure the user
   * can see — a `failed` episode with a code-less job must not fall back to a stuck timeline. Any other status opens it
   * only for a code the job carries, and always as a code: the server's `detail` prose is never read (D9). A cancelled
   * job is never a failure — `render_cancelled` is a job state, not an outcome.
   */
  readonly failure = computed<RenderFailure | null>(() => {
    const job = this.job();
    if (job === null || job.status === 'cancelled') return null;
    const code = job.errorCode ?? '';
    if (code === '' && job.status !== 'failed') return null;
    return { code, scenes: job.images?.skipped ?? [], detailCode: imageDetailCode(job) };
  });

  /** A job still being worked on: the only state that derives an elapsed time and arms the tick. */
  readonly active = computed(() => {
    const status = this.job()?.status;
    return status !== undefined && ACTIVE_STATUSES.includes(status);
  });

  /**
   * A `rendering` episode whose job row has not landed yet (the race of the failure-handling table): the page shows
   * `LoadingState` and calls `refresh()` once instead of spinning forever — after the re-read the status is known, so a
   * `story_approved` episode leaves the branch.
   */
  readonly needsRefetch = computed(() => {
    const episode = this.current();
    return episode !== null && episode.status === 'rendering' && episode.renderJob === null;
  });

  private subscription: RealtimeSubscription | null = null;
  private tick: number | null = null;
  private destroyed = false;

  constructor() {
    this.document.addEventListener('visibilitychange', this.onVisibility);
    effect(() => {
      // The tick is a function of the job's state and of the tab being visible: it starts when both hold, and the
      // first terminal status (or a hidden tab) stops it. Reading `active()` also re-arms it for a newly armed episode.
      const armed = !this.destroyed && this.active() && this.visibleState();
      untracked(() => { this.setTick(armed); });
    });
    effect(() => {
      const job = this.job();
      if (job === null) return;
      untracked(() => { this.stampQueuedSince(job); });
    });
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.document.removeEventListener('visibilitychange', this.onVisibility);
      this.subscription?.unsubscribe();
      this.subscription = null;
      this.setTick(false);
      this.queuedSinceState.set(null);
    });
  }

  /**
   * Arms the view for one episode: the realtime registration first — it keeps this episode's key live for the hub (the
   * 10.5.2-T1 discipline; `RealtimeService` stores an episode's `RenderProgress` with or without a subscriber, so the
   * handler this store passes is a no-op by design) — and then the tick (through the effect above). Idempotent per
   * episode: a second call for the same id does nothing, and a call for a different id (a `:id` reuse) drops the
   * previous baseline, timer and subscription first.
   *
   * No read happens here on purpose. `EpisodeDetailStore.load()` always carries `USER_ACTION`, which an automatic
   * render-state read must not send (WR11/D7), and the page's own init already loads this id's detail; this store reads
   * only when it is told to (`refresh()`). The handler stays free of payload parsing: the progress map is a signal this
   * store reads, and the detail re-read on `EpisodeUpdated`/`resync` is the page's own subscription's (that store also
   * owns the id), so this store adds no HTTP of its own.
   */
  start(episodeId: string): void {
    if (episodeId === '' || this.destroyed) return;
    if (this.episodeId() === episodeId && this.subscription !== null) return;
    this.subscription?.unsubscribe();
    this.subscription = null;
    this.setTick(false);
    // The registration is made before the signal writes below, so the store never holds an id it did not register; the
    // progress of this episode reaches it through the map either way.
    const subscription = this.realtime.subscribeEpisode(episodeId, () => {});
    this.queuedSinceState.set(null);
    this.nowState.set(Date.now());
    this.episodeId.set(episodeId);
    this.subscription = subscription;
  }

  /** One background re-read of the detail: never `USER_ACTION`, so an unwatched tab does not refresh (WR11/D7). */
  refresh(): void {
    if (this.destroyed) return;
    void this.detail.refetch();
  }

  /** Keeps `submittedAt` or a terminal status from leaving a stale baseline behind (the Design's `queuedSince` rule). */
  private stampQueuedSince(job: RenderJob): void {
    if (job.submittedAt !== null || !ACTIVE_STATUSES.includes(job.status)) {
      this.queuedSinceState.set(null);
      return;
    }
    if (job.status === 'queued' && this.queuedSinceState() === null) this.queuedSinceState.set(Date.now());
  }

  private setTick(armed: boolean): void {
    if (armed) {
      if (this.tick === null) {
        // The clock is stamped at the instant the tick is armed: a job that became active long after `start()` would
        // otherwise show that older instant until the interval's first tick, one second later.
        this.nowState.set(Date.now());
        this.tick = window.setInterval(() => { this.nowState.set(Date.now()); }, TICK_MS);
      }
      return;
    }
    if (this.tick !== null) {
      window.clearInterval(this.tick);
      this.tick = null;
    }
  }

  private readonly onVisibility = (): void => {
    const visible = this.document.visibilityState === 'visible';
    this.visibleState.set(visible);
    // Coming back stamps the wall clock at once: the interval re-arms through the effect, but until its first tick the
    // view would show the instant the tab was hidden, stale by the whole hidden stretch.
    if (visible) this.nowState.set(Date.now());
  };
}
