import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, InjectionToken, computed, effect, inject, signal, untracked } from '@angular/core';
import { Observable, Subject, filter } from 'rxjs';
import { AppErrorMapper, type AppError } from '@core/errors';
import { ActivityTracker, SessionStore, type SessionScoped } from '@core/session';
import { nextDelayMs } from './backoff';
import { HUB_CONNECTION_FACTORY, type HubConnectionPort } from './hub-connection.port';
import { HubTicketRepository } from './hub-ticket.repository';
import {
  RENDER_STAGES,
  WIRE_TO_NAME,
  type RealtimeEvent,
  type RealtimeEventName,
  type RealtimeEventOf,
  type RealtimeIds,
  type RealtimeState,
  type RenderProgress,
  type RenderStage,
} from './realtime.model';
import { RealtimeSubscriptions, type RealtimeSubscription } from './realtime-subscriptions';

/** Jitter source of the reconnect ladder; a spec provides a deterministic one. */
export const REALTIME_RANDOM = new InjectionToken<() => number>('REALTIME_RANDOM', {
  providedIn: 'root',
  factory: () => Math.random,
});

const HUB_PATH = '/hubs/episodes';
const ID_KEYS = ['seriesId', 'episodeId', 'renderJobId', 'publicationId', 'storyVersionId'] as const;
/** Longest id a server event may carry; real ids are 36-char GUIDs. */
const MAX_ID_LENGTH = 64;
/** The ladder resets only after the link stayed up this long (a flapping hub keeps climbing). */
export const STABLE_RESET_MS = 30_000;
/** Minimum spacing of the resyncs a (re)connect emits. */
export const MIN_RESYNC_INTERVAL_MS = 5_000;
/** The progress map keeps at most this many episodes; the oldest is evicted. */
export const MAX_PROGRESS_ENTRIES = 256;

interface SocketLife {
  started: boolean;
  closedDuringStart: boolean;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isAppError(value: unknown): value is AppError {
  return isRecord(value) && 'code' in value && 'status' in value && 'kind' in value;
}

/** Copies only the known id members that are non-empty strings; everything else the server sent is ignored. */
function readIds(raw: unknown): RealtimeIds {
  if (!isRecord(raw)) return {};
  const ids: Record<string, string> = {};
  for (const key of ID_KEYS) {
    const value = raw[key];
    if (typeof value === 'string' && value.length > 0 && value.length <= MAX_ID_LENGTH) ids[key] = value;
  }
  return ids;
}

function readProgress(raw: unknown): RenderProgress | null {
  if (!isRecord(raw)) return null;
  const { stage, percent } = raw;
  if (typeof stage !== 'string' || !(RENDER_STAGES as readonly string[]).includes(stage)) return null;
  if (typeof percent !== 'number' || !Number.isFinite(percent)) return null;
  return { stage: stage as RenderStage, percent: Math.min(100, Math.max(0, percent)) };
}

function matchesIds(actual: RealtimeIds, wanted: Partial<RealtimeIds> | undefined): boolean {
  if (wanted === undefined) return true;
  return ID_KEYS.every((key) => wanted[key] === undefined || wanted[key] === actual[key]);
}

/**
 * The one hub connection of the signed-in user (README D8). Contract:
 * - connect = fresh access token → one ticket → `start('/hubs/episodes?ticket=…')` → `connected`, one `resync`. The URL is
 *   a local of that call: never stored, logged or put in an error. The access token never reaches a URL.
 * - every close is the same path: backoff 1→30 s (+0–20 %), fresh token, fresh ticket; an idle user (D7) pauses instead,
 *   with no ticket request; `resume()` is the user action that continues. The ladder restarts at 1 s only after the link
 *   stayed up `STABLE_RESET_MS`; reconnect resyncs are spaced `MIN_RESYNC_INTERVAL_MS` (a suppressed one trails once).
 * - timers: the reconnect, stable-reset and trailing-resync timers and the coalescing windows are cleared by
 *   `stop()`/`reset()`/`pause()` and on destroy.
 * - generations: every connect captures the session generation and a private connection generation; each await and
 *   each socket callback re-checks both, so a late token/ticket/start of a gone session or a stopped run is discarded
 *   and its socket closed.
 * - refetches triggered by `resync`/events are the subscribers' own calls and carry no `USER_ACTION` (WR11).
 * `SessionStore` reaches `reset()` through the lazy `SESSION_SCOPED` entry of `provideRealtime()`.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService implements SessionScoped {
  private readonly tickets = inject(HubTicketRepository);
  private readonly connectionFactory = inject(HUB_CONNECTION_FACTORY);
  private readonly session = inject(SessionStore);
  private readonly activity = inject(ActivityTracker);
  private readonly mapper = inject(AppErrorMapper);
  private readonly random = inject(REALTIME_RANDOM);
  private readonly document = inject(DOCUMENT);

  private readonly stateSignal = signal<RealtimeState>('idle');
  readonly state = this.stateSignal.asReadonly();
  readonly connected = computed(() => this.state() === 'connected');
  readonly paused = computed(() => this.state() === 'paused');
  private readonly resyncSignal = signal(0);
  readonly resync = this.resyncSignal.asReadonly();
  private readonly progressSignal = signal<ReadonlyMap<string, RenderProgress>>(new Map());
  readonly progress = this.progressSignal.asReadonly();

  private readonly subscriptions = new RealtimeSubscriptions();
  private readonly events = new Subject<RealtimeEvent>();
  private connection: HubConnectionPort | null = null;
  /** Bumped by every invalidation and every connect; a flight or timer holding an older value is stale. */
  private connectionGeneration = 0;
  private connecting = false;
  private attempt = 0;
  private backoffTimer: number | null = null;
  private visibilityAttached = false;
  /** The session generation the current run belongs to, and the one a bare `stop()` happened in. */
  private runGeneration = -1;
  private stoppedGeneration = -1;
  private stableTimer: number | null = null;
  private resyncTimer: number | null = null;
  private lastConnectResyncAt = Number.NEGATIVE_INFINITY;
  private readonly pendingProgress = new Map<string, RenderProgress>();
  private progressFlushScheduled = false;
  private progressEpoch = 0;

  private readonly onVisibility = (): void => {
    if (this.document.visibilityState === 'visible' && this.stateSignal() === 'connected') this.signalResync();
  };

  constructor() {
    // WR8: start on a full authenticated session, reset on anonymous/ended, ignore the transient restoring/refreshing.
    effect(() => {
      const status = this.session.status();
      const mode = this.session.mode();
      const generation = this.session.generation();
      untracked(() => {
        if (status === 'anonymous' || status === 'ended') {
          this.reset();
          return;
        }
        if (status !== 'authenticated' || mode !== 'full') return;
        // Another identity replaced the session without passing through anonymous: nothing of any started run (even
        // a stopped one) survives into it.
        if (this.runGeneration !== -1 && this.runGeneration !== generation) this.reset();
        // A bare stop() (refresh failure) must not be undone by the status flicker that caused it.
        if (this.stateSignal() === 'stopped' && generation === this.stoppedGeneration) return;
        this.start();
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.invalidate();
      this.closeConnection();
      this.subscriptions.clear();
      this.detachVisibility();
      this.events.complete();
    });
  }

  /** Idempotent; a no-op unless `mode === 'full' && status === 'authenticated'` (WR8). */
  start(): void {
    if (this.session.mode() !== 'full' || this.session.status() !== 'authenticated') return;
    if (this.running()) return;
    this.attempt = 0;
    this.lastConnectResyncAt = Number.NEGATIVE_INFINITY;
    this.runGeneration = this.session.generation();
    this.attachVisibility();
    void this.connect(false);
  }

  /** Closes the socket, clears timers and subscriptions, detaches listeners; `state` becomes `stopped`. */
  stop(): void {
    this.invalidate();
    this.closeConnection();
    this.subscriptions.clear();
    this.detachVisibility();
    this.stoppedGeneration = this.session.generation();
    this.stateSignal.set('stopped');
  }

  /** The `SESSION_SCOPED` entry point: `stop()` plus a new empty `progress` map and `idle`. Idempotent. */
  reset(): void {
    this.invalidate();
    this.closeConnection();
    this.subscriptions.clear();
    this.detachVisibility();
    this.runGeneration = -1;
    this.progressSignal.set(new Map());
    this.stateSignal.set('idle');
  }

  /** The D7 idle gate: the socket is closed and nothing is scheduled until `resume()`. */
  pause(): void {
    this.invalidate();
    this.closeConnection();
    // Registrations stay for resume(); an open 250 ms window must not fire a refetch while paused.
    this.subscriptions.cancelPending();
    this.stateSignal.set('paused');
  }

  /** User action: refresh the token, new ticket, connect, one `resync`. */
  resume(): void {
    if (this.stateSignal() !== 'paused') return;
    this.attempt = 0;
    this.lastConnectResyncAt = Number.NEGATIVE_INFINITY;
    void this.connect(true);
  }

  /** Events of one kind, optionally narrowed to events that carry the given ids. */
  on<E extends RealtimeEventName>(name: E, ids?: Partial<RealtimeIds>): Observable<RealtimeEventOf<E>> {
    return this.events.pipe(filter((event): event is RealtimeEventOf<E> => event.name === name && matchesIds(event.ids, ids)));
  }

  /** Local, ref-counted, coalesced: `handler` runs at most once per 250 ms for events about this episode and on resync. */
  subscribeEpisode(episodeId: string, handler: () => void): RealtimeSubscription {
    return this.subscriptions.add(episodeId, handler);
  }

  private running(): boolean {
    const state = this.stateSignal();
    return state === 'connecting' || state === 'connected' || state === 'backoff' || state === 'paused';
  }

  /** Detaches every in-flight flight and timer: whatever they hold is stale from here on. */
  private invalidate(): void {
    this.connectionGeneration += 1;
    this.connecting = false;
    this.clearBackoff();
    this.clearConnectionTimers();
    // A progress batch queued before this point must not publish after it.
    this.progressEpoch += 1;
    this.progressFlushScheduled = false;
    this.pendingProgress.clear();
  }

  private clearBackoff(): void {
    if (this.backoffTimer === null) return;
    window.clearTimeout(this.backoffTimer);
    this.backoffTimer = null;
  }

  private closeConnection(): void {
    const socket = this.connection;
    this.connection = null;
    this.closeSocket(socket);
  }

  private closeSocket(socket: HubConnectionPort | null): void {
    if (socket === null) return;
    // The socket is going away either way; a failing stop has nothing to report.
    socket.stop().catch(() => undefined);
  }

  private attachVisibility(): void {
    if (this.visibilityAttached) return;
    this.visibilityAttached = true;
    this.document.addEventListener('visibilitychange', this.onVisibility);
  }

  private detachVisibility(): void {
    if (!this.visibilityAttached) return;
    this.visibilityAttached = false;
    this.document.removeEventListener('visibilitychange', this.onVisibility);
  }

  private signalResync(): void {
    this.resyncSignal.update((n) => n + 1);
    this.subscriptions.notifyAll();
  }

  /** (Re)connect resync, at most one per `MIN_RESYNC_INTERVAL_MS`; a suppressed one trails once the link stays up. */
  private resyncAfterConnect(): void {
    const now = Date.now();
    const due = this.lastConnectResyncAt + MIN_RESYNC_INTERVAL_MS;
    if (now >= due) {
      this.lastConnectResyncAt = now;
      this.signalResync();
      return;
    }
    this.clearResyncTimer();
    const flight = this.connectionGeneration;
    const sessionGeneration = this.session.generation();
    this.resyncTimer = window.setTimeout(() => {
      this.resyncTimer = null;
      if (flight !== this.connectionGeneration || sessionGeneration !== this.session.generation()) return;
      if (this.stateSignal() !== 'connected') return;
      this.lastConnectResyncAt = Date.now();
      this.signalResync();
    }, due - now);
  }

  /** The ladder restarts at 1 s only after the link stayed up for `STABLE_RESET_MS`; a quick drop keeps climbing. */
  private armStableReset(flight: number): void {
    this.clearStableTimer();
    const sessionGeneration = this.session.generation();
    this.stableTimer = window.setTimeout(() => {
      this.stableTimer = null;
      if (flight !== this.connectionGeneration || sessionGeneration !== this.session.generation()) return;
      if (this.stateSignal() === 'connected') this.attempt = 0;
    }, STABLE_RESET_MS);
  }

  private clearStableTimer(): void {
    if (this.stableTimer === null) return;
    window.clearTimeout(this.stableTimer);
    this.stableTimer = null;
  }

  private clearResyncTimer(): void {
    if (this.resyncTimer === null) return;
    window.clearTimeout(this.resyncTimer);
    this.resyncTimer = null;
  }

  private clearConnectionTimers(): void {
    this.clearStableTimer();
    this.clearResyncTimer();
  }

  /**
   * Latest value per episode wins: a burst is merged into one new immutable map per microtask (readers keep their
   * snapshots), the map is capped at `MAX_PROGRESS_ENTRIES` with the oldest entry evicted first.
   */
  private queueProgress(episodeId: string, progress: RenderProgress): void {
    this.pendingProgress.delete(episodeId);
    this.pendingProgress.set(episodeId, progress);
    if (this.progressFlushScheduled) return;
    this.progressFlushScheduled = true;
    const epoch = this.progressEpoch;
    const sessionGeneration = this.session.generation();
    queueMicrotask(() => {
      if (epoch !== this.progressEpoch) return;
      this.progressFlushScheduled = false;
      if (sessionGeneration !== this.session.generation()) {
        this.pendingProgress.clear();
        return;
      }
      const next = new Map(this.progressSignal());
      for (const [id, value] of this.pendingProgress) {
        next.delete(id);
        next.set(id, value);
      }
      this.pendingProgress.clear();
      for (const id of next.keys()) {
        if (next.size <= MAX_PROGRESS_ENTRIES) break;
        next.delete(id);
      }
      this.progressSignal.set(next);
    });
  }

  /** Single flight: a second call while one is running (and not invalidated) does nothing. */
  private async connect(userAction: boolean): Promise<void> {
    if (this.connecting) return;
    this.connecting = true;
    this.connectionGeneration += 1;
    const flight = this.connectionGeneration;
    this.stateSignal.set('connecting');
    try {
      await this.runConnect(flight, this.session.generation(), userAction);
    } finally {
      if (flight === this.connectionGeneration) this.connecting = false;
    }
  }

  private async runConnect(flight: number, sessionGeneration: number, userAction: boolean): Promise<void> {
    const stale = (): boolean => flight !== this.connectionGeneration || sessionGeneration !== this.session.generation();
    let token = await this.acquireToken({ userAction, generation: sessionGeneration }, stale);
    if (token === null) return;

    // Round 0 is the normal attempt; round 1 exists only for the one retry after a 401 on the ticket route.
    for (let round = 0; round < 2; round += 1) {
      let ticket: string;
      try {
        ticket = (await this.tickets.issue()).ticket;
      } catch (cause) {
        if (stale()) return;
        const error = isAppError(cause) ? cause : this.mapper.from(cause);
        if (error.status === 401 && round === 0) {
          token = await this.acquireToken({ userAction, generation: sessionGeneration, rejectedToken: token }, stale);
          if (token === null) return;
          continue;
        }
        this.scheduleBackoff(error.status === 429 ? (error.retryAfterSec ?? 0) * 1000 : 0);
        return;
      }
      if (stale()) return;

      const socket = this.connectionFactory();
      const life = this.registerHandlers(socket, flight, sessionGeneration);
      this.connection = socket;
      try {
        await socket.start(`${HUB_PATH}?ticket=${encodeURIComponent(ticket)}`);
      } catch {
        if (this.connection === socket) this.connection = null;
        this.closeSocket(socket);
        if (stale()) return;
        // A failed start() is an ordinary close: in a browser a WS-upgrade 401 is a plain Error with no status, so no
        // handshake-specific retry exists. The next attempt takes a fresh ticket through the ladder.
        this.scheduleBackoff(0);
        return;
      }
      if (stale()) {
        // A late start after stop/reset/pause/new session: the socket must not outlive the run that asked for it.
        if (this.connection === socket) this.connection = null;
        this.closeSocket(socket);
        return;
      }
      life.started = true;
      if (life.closedDuringStart) {
        // The socket closed while start() was settling: the ordinary close path, once (no state 'connected').
        if (this.connection === socket) this.connection = null;
        this.closeSocket(socket);
        this.handleClose();
        return;
      }
      this.stateSignal.set('connected');
      this.armStableReset(flight);
      this.resyncAfterConnect();
      return;
    }
  }

  /**
   * A fresh access token for this flight, or `null` when the flight must end: it went stale, the session ended
   * (`stop()`, no reconnect), the user is idle and the token needs a refresh (`pause()`), or the refresh failed
   * transiently (backoff on the ladder, or `pause()` while idle). Terminal refresh failures are the ones
   * `SessionStore` itself ends the session for (`refresh_token_reused`, `session_revoked`): they leave the store
   * `ended`/`anonymous`, which is the only classification used here (README D8, 10.2.1-T1).
   */
  private async acquireToken(
    options: { userAction: boolean; generation: number; rejectedToken?: string },
    stale: () => boolean,
  ): Promise<string | null> {
    let token: string | null;
    try {
      token = await this.session.ensureFreshAccessToken({
        userAction: options.userAction,
        generation: options.generation,
        ...(options.rejectedToken === undefined ? {} : { rejectedToken: options.rejectedToken }),
      });
    } catch (cause) {
      const status = this.session.status();
      if (status === 'ended' || status === 'anonymous') {
        // Terminal: the store ended the session; no reconnect, the status effect's reset() follows.
        if (this.stateSignal() === 'connecting') this.stop();
        return null;
      }
      if (stale()) return null;
      const error = isAppError(cause) ? cause : this.mapper.from(cause);
      this.retryLater(error.status === 429 ? (error.retryAfterSec ?? 0) * 1000 : 0);
      return null;
    }
    if (stale()) return null;
    if (token === null) {
      if (this.activity.isActive()) this.stop();
      else this.pause();
      return null;
    }
    return token;
  }

  /** A transient failure before a ticket exists: wait on the ladder, or pause while the user is idle (D7). */
  private retryLater(minDelayMs: number): void {
    if (!this.activity.isActive()) {
      this.pause();
      return;
    }
    this.scheduleBackoff(minDelayMs);
  }

  /**
   * Every socket callback re-checks the connection generation and the session generation captured at connect time: a
   * stale socket is closed and neither dispatches nor schedules anything, even before the status effect has run
   * `reset()` for a replaced identity. A close that fires before `start()` settles is recorded in the returned
   * `SocketLife` and routed through the ordinary close path by the flight, exactly once.
   */
  private registerHandlers(socket: HubConnectionPort, flight: number, sessionGeneration: number): SocketLife {
    const life: SocketLife = { started: false, closedDuringStart: false };
    // A socket that is no longer the current one, or whose flight/session went stale, is closed (idempotently) and
    // nothing else happens: the current connection is only cleared when it is this very socket.
    const dropIfStale = (): boolean => {
      const current =
        this.connection === socket && flight === this.connectionGeneration && sessionGeneration === this.session.generation();
      if (current) return false;
      if (this.connection === socket) this.connection = null;
      this.closeSocket(socket);
      return true;
    };
    for (const wire of WIRE_TO_NAME.keys()) {
      socket.on(wire, (payload) => {
        if (dropIfStale()) return;
        this.dispatch(wire, payload);
      });
    }
    socket.onClose(() => {
      if (dropIfStale()) return;
      if (!life.started) {
        life.closedDuringStart = true;
        return;
      }
      this.connection = null;
      this.clearConnectionTimers();
      this.handleClose();
    });
    return life;
  }

  /** The one path for every close: server abort, family revocation, network drop. */
  private handleClose(): void {
    const state = this.stateSignal();
    if (state === 'stopped' || state === 'idle' || state === 'paused') return;
    if (!this.activity.isActive()) {
      this.pause();
      return;
    }
    this.scheduleBackoff(0);
  }

  private scheduleBackoff(minDelayMs: number): void {
    this.clearBackoff();
    const flight = this.connectionGeneration;
    const delay = Math.max(nextDelayMs(this.attempt, this.random), minDelayMs);
    this.attempt += 1;
    this.stateSignal.set('backoff');
    this.backoffTimer = window.setTimeout(() => {
      this.backoffTimer = null;
      if (flight !== this.connectionGeneration) return;
      void this.connect(false);
    }, delay);
  }

  /** Looks the name up before touching the payload: an unknown name never reaches the ids. */
  private dispatch(wire: string, payload: unknown): void {
    const name = WIRE_TO_NAME.get(wire);
    if (name === undefined || !isRecord(payload)) return;
    const ids = readIds(payload['ids']);
    if (name === 'RenderProgress') {
      const progress = readProgress(payload['data']);
      if (progress === null || ids.episodeId === undefined) return;
      this.queueProgress(ids.episodeId, progress);
      this.events.next({ name, ids, progress });
      return;
    }
    this.events.next({ name, ids });
    if (ids.episodeId !== undefined) this.subscriptions.notify(ids.episodeId);
    if (ids.seriesId !== undefined && ids.seriesId !== ids.episodeId) this.subscriptions.notify(ids.seriesId);
  }
}
