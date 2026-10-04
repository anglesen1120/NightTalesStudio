import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, inject, isSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { USER_ACTION, userAction } from '@core/http';
import { SessionApi, SessionStore, type TokenPair } from '@core/session';
import { FakeSessionApi, fakeJwt } from '../session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { HUB_CONNECTION_FACTORY } from './hub-connection.port';
import { HubTicketRepository } from './hub-ticket.repository';
import { provideRealtime } from './provide-realtime';
import { COALESCE_WINDOW_MS } from './realtime-subscriptions';
import {
  MAX_PROGRESS_ENTRIES,
  MIN_RESYNC_INTERVAL_MS,
  REALTIME_RANDOM,
  RealtimeService,
  STABLE_RESET_MS,
} from './realtime.service';
import { NAME_TO_WIRE, WIRE_TO_NAME, type RealtimeEvent } from './realtime.model';
import { FakeHubConnection } from './testing/fake-hub-connection';
import { FakeHubTicketRepository } from './testing/fake-hub-ticket.repository';

const T0 = Date.parse('2026-10-04T10:00:00Z');
const MINUTE = 60_000;
const DAY = 86_400_000;

interface Harness {
  readonly svc: RealtimeService;
  readonly store: SessionStore;
  readonly sessionApi: FakeSessionApi;
  readonly tickets: FakeHubTicketRepository;
  readonly sockets: FakeHubConnection[];
  /** The n-th socket the factory created (fails loudly when there is none). */
  socket(n: number): FakeHubConnection;
  readonly http: HttpTestingController;
  /** Start mode of the NEXT sockets the factory creates. */
  startMode: 'ok' | 'reject' | 'deferred';
  startError: unknown;
}

function setup(random: () => number = () => 0): Harness {
  const sessionApi = new FakeSessionApi();
  const tickets = new FakeHubTicketRepository();
  const sockets: FakeHubConnection[] = [];
  const harness = { startMode: 'ok', startError: new Error('socket failed') } as Pick<Harness, 'startMode' | 'startError'>;
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([
        { path: 'login', children: [] },
        { path: '**', children: [] },
      ]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SessionApi, useValue: sessionApi },
      provideRealtime(),
      // Later providers win: the fakes replace the HTTP repository and the SignalR factory of provideRealtime().
      { provide: HubTicketRepository, useValue: tickets },
      {
        provide: HUB_CONNECTION_FACTORY,
        useValue: () => {
          const socket = new FakeHubConnection();
          socket.startMode = harness.startMode;
          socket.startError = harness.startError;
          sockets.push(socket);
          return socket;
        },
      },
      { provide: REALTIME_RANDOM, useValue: random },
    ],
  });
  const store = TestBed.inject(SessionStore);
  const svc = TestBed.inject(RealtimeService);
  const socket = (n: number): FakeHubConnection => {
    const found = sockets[n];
    if (found === undefined) throw new Error(`no socket #${String(n)}`);
    return found;
  };
  return Object.assign(harness, { svc, store, sessionApi, tickets, sockets, socket, http: TestBed.inject(HttpTestingController) });
}

function tokens(expiresInMs = 15 * MINUTE, sub = 'user-1'): TokenPair {
  return { accessToken: fakeJwt('user', ['pwd'], sub), expiresAt: Date.now() + expiresInMs };
}

function signIn(h: Harness, expiresInMs = 15 * MINUTE, sub = 'user-1'): TokenPair {
  const pair = tokens(expiresInMs, sub);
  h.store.signIn(pair, 'user', ['pwd']);
  return pair;
}

async function settle(): Promise<void> {
  for (let i = 0; i < 40; i += 1) await Promise.resolve();
}

async function advance(ms: number): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  await settle();
}

/** Signs in, starts, and lets the first connect finish. */
async function connected(h: Harness): Promise<FakeHubConnection> {
  signIn(h);
  h.svc.start();
  await settle();
  expect(h.svc.state()).toBe('connected');
  return h.socket(0);
}

function envelope(event: string, ids: Record<string, string>, data: unknown = null): unknown {
  return { userId: 'u-1', event, ids, data };
}

function setVisibility(value: 'visible' | 'hidden'): void {
  vi.spyOn(document, 'visibilityState', 'get').mockReturnValue(value);
}

describe('RealtimeService', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    vi.setSystemTime(T0);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  describe('start', () => {
    it('start_IsNoOpUnlessAuthenticated', async () => {
      const h = setup();
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(0);
      expect(h.sockets).toHaveLength(0);
      expect(h.svc.state()).toBe('idle');

      signIn(h);
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(1);
      expect(h.svc.state()).toBe('connected');
    });

    it('start_IssuesOneTicketAndConnectsWebSocketsOnly', async () => {
      const h = setup();
      const socket = await connected(h);
      expect(h.tickets.calls).toBe(1);
      expect(h.sockets).toHaveLength(1);
      expect(socket.startedUrls).toEqual([`/hubs/episodes?ticket=${encodeURIComponent(FakeHubTicketRepository.ticketNumber(1))}`]);
      expect(socket.recordedOptions).toEqual({ transport: 'WebSockets', skipNegotiation: true });
      expect(h.svc.connected()).toBe(true);
      expect(h.svc.resync()).toBe(1);
      // Idempotent: a second start() neither asks for a ticket nor opens a socket.
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(1);
      expect(h.sockets).toHaveLength(1);
    });

    it('connect_DoesNotUseAccessTokenFactory', async () => {
      const h = setup();
      const pair = signIn(h);
      h.svc.start();
      await settle();
      const socket = h.socket(0);
      const [url] = socket.startedUrls;
      expect(url).not.toContain(pair.accessToken);
      expect(url).not.toContain('access_token');
      expect(Object.keys(socket)).not.toContain('accessTokenFactory');
      expect('accessTokenFactory' in socket).toBe(false);
    });

    it('enrolmentMode_StartIsNoOp', async () => {
      const h = setup();
      h.store.beginEnrolment('enrol-token');
      h.svc.start();
      TestBed.tick();
      await settle();
      expect(h.tickets.calls).toBe(0);
      expect(h.sockets).toHaveLength(0);
      expect(h.sessionApi.calls).toBe(0);
      expect(h.svc.state()).toBe('idle');
    });
  });

  describe('close and backoff', () => {
    it('close_SchedulesBackoff_1_2_4_8_16_30', async () => {
      const h = setup();
      const socket = await connected(h);
      h.startMode = 'reject';
      socket.fireClose();
      expect(h.svc.state()).toBe('backoff');
      let calls = h.tickets.calls;
      for (const delay of [1000, 2000, 4000, 8000, 16_000, 30_000, 30_000]) {
        await advance(delay - 1);
        expect(h.tickets.calls).toBe(calls);
        await advance(1);
        calls += 1;
        expect(h.tickets.calls).toBe(calls);
        expect(h.svc.state()).toBe('backoff');
      }
    });

    it('close_UsesJitterWithinTwentyPercent', async () => {
      const h = setup(() => 1);
      const socket = await connected(h);
      socket.fireClose();
      // random() = 1 → the upper bound: 1000 + 20 %.
      await advance(1199);
      expect(h.tickets.calls).toBe(1);
      await advance(1);
      expect(h.tickets.calls).toBe(2);
      expect(h.svc.state()).toBe('connected');
    });

    it('close_WithInactiveUser_PausesWithoutTicket', async () => {
      const h = setup();
      const socket = await connected(h);
      vi.setSystemTime(T0 + 14 * DAY);
      socket.fireClose();
      expect(h.svc.state()).toBe('paused');
      expect(h.svc.paused()).toBe(true);
      await advance(10 * MINUTE);
      expect(h.tickets.calls).toBe(1);
      expect(h.sockets).toHaveLength(1);
    });

    it('connectWhileIdleWithStaleToken_PausesWithoutTicket', async () => {
      const h = setup();
      signIn(h, 0);
      vi.setSystemTime(T0 + 14 * DAY);
      h.svc.start();
      await settle();
      expect(h.svc.state()).toBe('paused');
      expect(h.tickets.calls).toBe(0);
      expect(h.sessionApi.calls).toBe(0);
    });

    it('resume_RefreshesTicketsConnectsAndResyncsOnce', async () => {
      const h = setup();
      const socket = await connected(h);
      vi.setSystemTime(T0 + 14 * DAY);
      socket.fireClose();
      expect(h.svc.state()).toBe('paused');
      const resyncBefore = h.svc.resync();

      h.svc.resume();
      await settle();

      // The token had expired: the user action refreshed it once, then one ticket, one socket, one resync.
      expect(h.sessionApi.calls).toBe(1);
      expect(h.tickets.calls).toBe(2);
      expect(h.sockets).toHaveLength(2);
      expect(h.svc.state()).toBe('connected');
      expect(h.svc.resync()).toBe(resyncBefore + 1);
    });

    it('resume_IsNoOpUnlessPaused', async () => {
      const h = setup();
      await connected(h);
      h.svc.resume();
      await settle();
      expect(h.tickets.calls).toBe(1);
    });
  });

  describe('ticket errors', () => {
    it('ticketUnauthorized_RetriesExactlyOnceThenBacksOff', async () => {
      const h = setup();
      signIn(h);
      h.tickets.failures.push(FakeHubTicketRepository.unauthorized(), FakeHubTicketRepository.unauthorized());
      h.svc.start();
      await settle();
      // One extra refresh + one retry, not a loop; then the ladder continues.
      expect(h.tickets.calls).toBe(2);
      expect(h.sessionApi.calls).toBe(1);
      expect(h.sockets).toHaveLength(0);
      expect(h.svc.state()).toBe('backoff');
      await advance(1000);
      expect(h.tickets.calls).toBe(3);
      expect(h.svc.state()).toBe('connected');
    });

    it('ticketUnauthorized_ThenSuccess_ConnectsInTheSameAttempt', async () => {
      const h = setup();
      signIn(h);
      h.tickets.failures.push(FakeHubTicketRepository.unauthorized());
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(2);
      expect(h.svc.state()).toBe('connected');
    });

    it('handshakeFailure_IsAnOrdinaryClose_FreshTicketPerAttempt', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'reject'; // a browser's WS-upgrade 401 is a plain Error without statusCode
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(1);
      expect(h.sessionApi.calls).toBe(0); // no handshake-specific refresh + retry
      expect(h.svc.state()).toBe('backoff');
      await advance(1000);
      expect(h.tickets.calls).toBe(2);
      expect(h.sockets).toHaveLength(2);
      expect(h.svc.state()).toBe('backoff');
      expect(new Set(h.sockets.map((s) => s.startedUrls[0])).size).toBe(2);
      h.startMode = 'ok';
      await advance(2000);
      expect(h.tickets.calls).toBe(3);
      expect(h.svc.state()).toBe('connected');
    });

    it('ticketRateLimited_HonoursRetryAfter', async () => {
      const h = setup();
      signIn(h);
      h.tickets.failures.push(FakeHubTicketRepository.rateLimited(30));
      h.svc.start();
      await settle();
      expect(h.svc.state()).toBe('backoff');
      await advance(29_999);
      expect(h.tickets.calls).toBe(1);
      await advance(1);
      expect(h.tickets.calls).toBe(2);
      expect(h.svc.state()).toBe('connected');
    });

    it('ticketUnavailable_BacksOffWithoutStopping', async () => {
      const h = setup();
      signIn(h);
      h.tickets.failures.push(FakeHubTicketRepository.unavailable());
      h.svc.start();
      await settle();
      expect(h.svc.state()).toBe('backoff');
      await advance(1000);
      expect(h.svc.state()).toBe('connected');
    });

    it.each(['revoked', 'reused'] as const)('refreshFailure_StopsAndNeverReconnects (terminal %s)', async (mode) => {
      const h = setup();
      signIn(h, 0);
      h.sessionApi.mode = mode;
      h.svc.start();
      await settle();
      expect(['stopped', 'idle']).toContain(h.svc.state());
      TestBed.tick();
      await settle();
      await advance(10 * MINUTE);
      expect(h.svc.state()).toBe('idle'); // the session end reset the service; nothing restarted it
      expect(h.store.status()).toBe('ended');
      expect(h.sessionApi.calls).toBe(1);
      expect(h.tickets.calls).toBe(0);
      expect(h.sockets).toHaveLength(0);
    });

    it.each(['network', 'server'] as const)('refreshFailure_Transient_%s_BacksOffThenReconnects', async (mode) => {
      const h = setup();
      signIn(h, 0);
      h.sessionApi.mode = mode;
      h.svc.start();
      await settle();
      // Not terminal: the session stays authenticated, realtime waits on the ladder with zero tickets.
      expect(h.store.status()).toBe('authenticated');
      expect(h.svc.state()).toBe('backoff');
      expect(h.tickets.calls).toBe(0);
      await advance(999);
      expect(h.sessionApi.calls).toBe(1);
      h.sessionApi.mode = 'ok';
      await advance(1);
      expect(h.sessionApi.calls).toBe(2);
      expect(h.tickets.calls).toBe(1);
      expect(h.sockets).toHaveLength(1);
      expect(h.svc.state()).toBe('connected');
      expect(h.svc.resync()).toBe(1);
    });

    it('refreshFailure_TransientBurst_WalksTheLadder_NeverMoreThanOneAttemptPerStep_ZeroTickets', async () => {
      const h = setup();
      signIn(h, 0);
      h.sessionApi.mode = 'network';
      h.svc.start();
      await settle();
      let calls = h.sessionApi.calls;
      expect(calls).toBe(1);
      for (const delay of [1000, 2000, 4000, 8000, 16_000, 30_000, 30_000]) {
        await advance(delay - 1);
        expect(h.sessionApi.calls).toBe(calls);
        await advance(1);
        calls += 1;
        expect(h.sessionApi.calls).toBe(calls);
        expect(h.svc.state()).toBe('backoff');
      }
      expect(h.tickets.calls).toBe(0);
      expect(h.sockets).toHaveLength(0);
      expect(h.store.status()).toBe('authenticated');
      h.sessionApi.mode = 'ok';
      await advance(30_000);
      expect(h.tickets.calls).toBe(1); // one ticket for the attempt that finally got a token
      expect(h.svc.state()).toBe('connected');
    });

    it('refreshFailure_RateLimited_HonoursRetryAfter', async () => {
      const h = setup();
      signIn(h, 0);
      h.sessionApi.mode = 'deferred';
      h.svc.start();
      await settle();
      h.sessionApi.rejectNext({ code: 'rate_limited', status: 429, kind: 'rate_limit', fieldErrors: {}, retryAfterSec: 30 });
      await settle();
      expect(h.svc.state()).toBe('backoff');
      await advance(29_999);
      expect(h.sessionApi.calls).toBe(1);
      h.sessionApi.mode = 'ok';
      await advance(1);
      expect(h.sessionApi.calls).toBe(2);
      expect(h.svc.state()).toBe('connected');
    });
  });

  describe('visibility', () => {
    it('visibilityVisible_EmitsOneResync', async () => {
      const h = setup();
      await connected(h);
      setVisibility('visible');
      const before = h.svc.resync();
      document.dispatchEvent(new Event('visibilitychange'));
      expect(h.svc.resync()).toBe(before + 1);
      expect(h.sockets).toHaveLength(1);
      expect(h.tickets.calls).toBe(1);
    });

    it('visibilityHidden_EmitsNothing', async () => {
      const h = setup();
      await connected(h);
      setVisibility('hidden');
      const before = h.svc.resync();
      document.dispatchEvent(new Event('visibilitychange'));
      expect(h.svc.resync()).toBe(before);
    });

    it('visibilityVisible_WhileNotConnected_EmitsNothing', async () => {
      const h = setup();
      const socket = await connected(h);
      h.startMode = 'reject';
      socket.fireClose();
      setVisibility('visible');
      const before = h.svc.resync();
      document.dispatchEvent(new Event('visibilitychange'));
      expect(h.svc.resync()).toBe(before);
    });

    it('stop_DetachesTheVisibilityListener', async () => {
      const h = setup();
      await connected(h);
      h.svc.stop();
      setVisibility('visible');
      const before = h.svc.resync();
      document.dispatchEvent(new Event('visibilitychange'));
      expect(h.svc.resync()).toBe(before);
    });
  });

  describe('events', () => {
    it('renderProgress_UpdatesMapWithoutHttp', async () => {
      const h = setup();
      const socket = await connected(h);
      const before = h.svc.progress();
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 42 }));
      await settle();
      expect(h.svc.progress().get('ep-1')).toEqual({ stage: 'voice', percent: 42 });
      // A new Map each time: consumers reading the signal see a changed value, nothing mutates in place.
      expect(h.svc.progress()).not.toBe(before);
      expect(before.size).toBe(0);
      h.http.verify();
    });

    it('renderProgress_LastEventWinsAndAStageChangeReplacesPercent', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 90 }));
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'images', percent: 5 }));
      await settle();
      expect(h.svc.progress().get('ep-1')).toEqual({ stage: 'images', percent: 5 });
    });

    it('renderProgress_UnknownStageIsDroppedAndClamped', async () => {
      const h = setup();
      const console_ = ['log', 'info', 'warn', 'error', 'debug'].map((name) =>
        vi.spyOn(console, name as 'log').mockImplementation(() => undefined),
      );
      const socket = await connected(h);
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'nope', percent: 10 }));
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 'x' }));
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, null));
      socket.fire('RenderProgress', envelope('render_progress', {}, { stage: 'voice', percent: 10 }));
      socket.fire('RenderProgress', 'not an object');
      await settle();
      expect(h.svc.progress().size).toBe(0);

      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'images', percent: 150 }));
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-2' }, { stage: 'images', percent: -5 }));
      await settle();
      expect(h.svc.progress().get('ep-1')?.percent).toBe(100);
      expect(h.svc.progress().get('ep-2')?.percent).toBe(0);
      for (const spy of console_) expect(spy).not.toHaveBeenCalled();
    });

    it('unknownEventName_IsDroppedBeforeAnyPayloadAccess_AndLogsNothing', async () => {
      const spies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((name) =>
        vi.spyOn(console, name).mockImplementation(() => undefined),
      );
      const h = setup();
      const socket = await connected(h);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);
      const seen: RealtimeEvent[] = [];
      h.svc.on('EpisodeUpdated').subscribe((event) => seen.push(event));

      // The service registers exactly the names of WIRE_TO_NAME.
      expect([...socket.registeredNames].sort()).toEqual([...WIRE_TO_NAME.keys()].sort());
      let reads = 0;
      const hostile = new Proxy(
        {},
        {
          get: () => {
            reads += 1;
            return undefined;
          },
        },
      );
      // fireRaw goes through the registered-handler lookup of the real library: unknown names reach no handler.
      for (const name of ['EpisodeUpdatedV2', '__proto__', 'constructor', 'toString', 'hasOwnProperty', '']) {
        expect(socket.fireRaw(name, hostile), name).toBe(0);
      }
      await advance(250);
      expect(reads).toBe(0);
      expect(handler).not.toHaveBeenCalled();
      expect(seen).toEqual([]);

      // Control: the same seam does reach the handler (and the payload) for a registered name, case-insensitively.
      expect(socket.fireRaw('episodeupdated', hostile)).toBe(1);
      expect(reads).toBeGreaterThan(0);
      for (const spy of spies) expect(spy).not.toHaveBeenCalled();
    });

    it('canonicalPascalCaseNames_MapToInternalNames', async () => {
      const h = setup();
      const socket = await connected(h);
      const seen: string[] = [];
      for (const name of Object.keys(NAME_TO_WIRE) as (keyof typeof NAME_TO_WIRE)[]) {
        expect(WIRE_TO_NAME.get(name)).toBe(name);
        h.svc.on(name).subscribe(() => seen.push(name));
      }
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      socket.fire('PublicationUpdated', envelope('publication_updated', { publicationId: 'p-1' }));
      socket.fire('CreditBalanceChanged', envelope('credit_balance_changed', {}));
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 1 }));
      expect(seen).toEqual(['EpisodeUpdated', 'PublicationUpdated', 'CreditBalanceChanged', 'RenderProgress']);
    });

    it('snakeCaseAliases_ResolveToTheSameNames', async () => {
      expect(WIRE_TO_NAME.get('render_progress')).toBe('RenderProgress');
      expect(WIRE_TO_NAME.get('episode_updated')).toBe('EpisodeUpdated');
      expect(WIRE_TO_NAME.get('publication_updated')).toBe('PublicationUpdated');
      expect(WIRE_TO_NAME.get('credit_balance_changed')).toBe('CreditBalanceChanged');
      expect(WIRE_TO_NAME.get('render_progressV2')).toBeUndefined();

      const h = setup();
      const socket = await connected(h);
      const seen: string[] = [];
      h.svc.on('EpisodeUpdated').subscribe((event) => seen.push(event.name));
      socket.fire('render_progress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 7 }));
      socket.fire('episode_updated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await settle();
      expect(h.svc.progress().get('ep-1')).toEqual({ stage: 'voice', percent: 7 });
      expect(seen).toEqual(['EpisodeUpdated']);
    });

    it('on_FiltersByIds', async () => {
      const h = setup();
      const socket = await connected(h);
      const seen: (string | undefined)[] = [];
      h.svc.on('EpisodeUpdated', { episodeId: 'ep-1' }).subscribe((event) => seen.push(event.ids.episodeId));
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-2' }));
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      expect(seen).toEqual(['ep-1']);
    });
  });

  describe('subscriptions', () => {
    it('episodeUpdated_NotifiesSubscribersOncePer250ms', async () => {
      const h = setup();
      const socket = await connected(h);
      const one = vi.fn();
      const other = vi.fn();
      h.svc.subscribeEpisode('ep-1', one);
      h.svc.subscribeEpisode('ep-2', other);
      for (let i = 0; i < 3; i += 1) socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(249);
      expect(one).not.toHaveBeenCalled();
      await advance(1);
      expect(one).toHaveBeenCalledTimes(1);
      expect(other).not.toHaveBeenCalled();
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(250);
      expect(one).toHaveBeenCalledTimes(2);
    });

    it('subscribeEpisode_FiltersLocallyAndSendsNoHubRpc', async () => {
      const h = setup();
      const socket = await connected(h);
      const matching = vi.fn();
      h.svc.subscribeEpisode('ep-1', matching);
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-9' }));
      await advance(250);
      expect(matching).not.toHaveBeenCalled();
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(250);
      expect(matching).toHaveBeenCalledTimes(1);
      // The port has no join/leave/invoke: the server joins `user:{id}` by itself.
      expect(['invoke', 'send', 'joinEpisode', 'leaveEpisode'].filter((name) => name in socket)).toEqual([]);
      expect(socket.startedUrls).toHaveLength(1);
    });

    it('unsubscribe_DuringOpenWindowDoesNotCallHandler', async () => {
      const h = setup();
      const socket = await connected(h);
      const handler = vi.fn();
      const subscription = h.svc.subscribeEpisode('ep-1', handler);
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(100);
      subscription.unsubscribe();
      await advance(250);
      expect(handler).not.toHaveBeenCalled();
    });

    it('resync_NotifiesLiveSubscribersOnceAfterReconnect', async () => {
      const h = setup();
      const socket = await connected(h);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);
      socket.fireClose();
      await advance(1000);
      expect(h.svc.state()).toBe('connected');
      expect(handler).not.toHaveBeenCalled();
      await advance(MIN_RESYNC_INTERVAL_MS); // resyncs of reconnects are spaced: this one trails the previous
      await advance(250);
      expect(handler).toHaveBeenCalledTimes(1);
    });

    it('backgroundRefetch_CarriesNoUserActionFlag', async () => {
      const h = setup();
      const socket = await connected(h);
      const client = TestBed.inject(HttpClient);
      // What a store does on resync/EpisodeUpdated: a plain GET (no flag). A user click adds `userAction()` (WR11).
      h.svc.subscribeEpisode('ep-1', () => {
        client.get('/api/v1/episodes/ep-1').subscribe();
      });
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(250);
      const background = h.http.expectOne('/api/v1/episodes/ep-1');
      expect(background.request.context.get(USER_ACTION)).toBe(false);
      background.flush({});

      client.get('/api/v1/episodes/ep-1', { context: userAction() }).subscribe();
      const click = h.http.expectOne('/api/v1/episodes/ep-1');
      expect(click.request.context.get(USER_ACTION)).toBe(true);
      click.flush({});
      h.http.verify();
    });
  });

  describe('stop and reset', () => {
    it('stop_ClearsTimersAndSubscriptions', async () => {
      const h = setup();
      const socket = await connected(h);
      const idleTimers = vi.getTimerCount();
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      h.startMode = 'reject';
      socket.fireClose();
      expect(vi.getTimerCount()).toBeGreaterThan(idleTimers);

      h.svc.stop();
      expect(vi.getTimerCount()).toBeLessThanOrEqual(idleTimers);
      await advance(60_000);
      expect(handler).not.toHaveBeenCalled();
      expect(h.tickets.calls).toBe(1);
      expect(h.svc.state()).toBe('stopped');
    });

    it('stop_SetsStopped_reset_SetsIdle', async () => {
      const h = setup();
      const socket = await connected(h);
      h.svc.stop();
      expect(h.svc.state()).toBe('stopped');
      expect(socket.stopCalls).toBe(1);
      h.svc.reset();
      expect(h.svc.state()).toBe('idle');
    });

    it('reset_ClearsProgressAndReturnsIdle', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 3 }));
      await settle();
      const previous = h.svc.progress();
      expect(previous.size).toBe(1);
      h.svc.reset();
      expect(h.svc.progress().size).toBe(0);
      expect(h.svc.progress()).not.toBe(previous);
      expect(previous.size).toBe(1);
      expect(h.svc.state()).toBe('idle');
    });

    it('reset_IsIdempotent', async () => {
      const h = setup();
      const socket = await connected(h);
      h.svc.reset();
      h.svc.reset();
      expect(socket.stopCalls).toBe(1);
      expect(h.svc.state()).toBe('idle');
      expect(h.svc.progress().size).toBe(0);
    });
  });

  describe('session status', () => {
    it('statusRefreshing_KeepsConnection', async () => {
      const h = setup();
      const socket = await connected(h);
      const current = h.store.accessToken() ?? '';
      h.sessionApi.mode = 'deferred';
      const refresh = h.store.ensureFreshAccessToken({ rejectedToken: current });
      await settle();
      expect(h.store.status()).toBe('refreshing');
      TestBed.tick();
      await settle();
      expect(socket.stopCalls).toBe(0);
      expect(h.svc.state()).toBe('connected');

      h.sessionApi.resolveNext(tokens());
      await refresh;
      TestBed.tick();
      await settle();
      expect(h.store.status()).toBe('authenticated');
      expect(socket.stopCalls).toBe(0);
      expect(h.sockets).toHaveLength(1);
      expect(h.tickets.calls).toBe(1);
      expect(h.svc.state()).toBe('connected');
    });

    it('statusEnded_StopsAndClearSubscriptions', async () => {
      const h = setup();
      const socket = await connected(h);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 3 }));

      h.store.signOut('revoked');
      TestBed.tick();
      await settle();
      expect(h.store.status()).toBe('ended');
      expect(h.svc.state()).toBe('idle');
      expect(h.svc.progress().size).toBe(0);
      expect(socket.stopCalls).toBeGreaterThanOrEqual(1);

      // A later user on the same device never receives the earlier user's subscription.
      signIn(h, 15 * MINUTE, 'user-2');
      TestBed.tick();
      await settle();
      expect(h.svc.state()).toBe('connected');
      h.socket(1).fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(250);
      expect(handler).not.toHaveBeenCalled();
      expect(h.svc.progress().size).toBe(0);
    });

    it('statusAuthenticated_AfterStopStartsAgain', async () => {
      const h = setup();
      await connected(h);
      h.svc.stop();
      expect(h.svc.state()).toBe('stopped');
      // Same session: a status flicker alone does not undo a bare stop().
      TestBed.tick();
      await settle();
      expect(h.svc.state()).toBe('stopped');

      signIn(h, 15 * MINUTE, 'user-2');
      TestBed.tick();
      await settle();
      expect(h.svc.state()).toBe('connected');
      expect(h.sockets).toHaveLength(2);
      expect(h.tickets.calls).toBe(2);
    });

    it('identitySwapWithoutAnonymous_DropsTheOldRun', async () => {
      const h = setup();
      const socket = await connected(h);
      signIn(h, 15 * MINUTE, 'user-2');
      TestBed.tick();
      await settle();
      expect(socket.stopCalls).toBeGreaterThanOrEqual(1);
      expect(h.sockets).toHaveLength(2);
      expect(h.svc.state()).toBe('connected');
    });
  });

  describe('late completions', () => {
    it('lateTicket_AfterStop_OpensNoSocket', async () => {
      const h = setup();
      signIn(h);
      h.tickets.deferred = true;
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(1);
      h.svc.stop();
      h.tickets.resolveNext();
      await settle();
      expect(h.sockets).toHaveLength(0);
      expect(h.svc.state()).toBe('stopped');
      expect(h.svc.resync()).toBe(0);
    });

    it('lateStart_AfterStop_ClosesTheSocket', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'deferred';
      h.svc.start();
      await settle();
      const socket = h.socket(0);
      expect(socket.startedUrls).toHaveLength(1);
      h.svc.stop();
      const stopsAtStop = socket.stopCalls;
      socket.resolveStart();
      await settle();
      expect(socket.stopCalls).toBeGreaterThan(stopsAtStop);
      expect(h.svc.state()).toBe('stopped');
      expect(h.svc.resync()).toBe(0);
    });

    it('lateStart_AfterPause_ClosesTheSocket', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'deferred';
      h.svc.start();
      await settle();
      const socket = h.socket(0);
      h.svc.pause();
      socket.resolveStart();
      await settle();
      expect(socket.stopCalls).toBeGreaterThanOrEqual(2);
      expect(h.svc.state()).toBe('paused');
      expect(h.svc.resync()).toBe(0);
    });

    it('lateTicket_AfterNewUserSignIn_NeverReachesTheNewSession', async () => {
      const h = setup();
      signIn(h);
      h.tickets.deferred = true;
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(1);

      // User B signs in while A's ticket is still in flight; the effect restarts for B.
      signIn(h, 15 * MINUTE, 'user-2');
      TestBed.tick();
      await settle();
      expect(h.tickets.calls).toBe(2);

      h.tickets.resolveNext(); // A's ticket arrives late
      await settle();
      expect(h.sockets).toHaveLength(0);
      h.tickets.resolveNext(); // B's own ticket
      await settle();
      expect(h.sockets).toHaveLength(1);
      expect(h.socket(0).startedUrls[0]).toContain(FakeHubTicketRepository.ticketNumber(2));
      expect(h.svc.resync()).toBe(1);
    });

    it('lateRefreshFailure_AfterStop_DoesNotOverrideState', async () => {
      const h = setup();
      signIn(h, 0);
      h.sessionApi.mode = 'deferred';
      h.svc.start();
      await settle();
      h.svc.pause();
      h.sessionApi.rejectNext({ code: 'network_error', status: 0, kind: 'network', fieldErrors: {} });
      await settle();
      expect(h.svc.state()).toBe('paused');
    });

    it('zeroTimersAfterStopDuringBackoffAndDestroy', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fireClose();
      expect(h.svc.state()).toBe('backoff');
      TestBed.resetTestingModule();
      await advance(60_000);
      expect(h.tickets.calls).toBe(1);
    });
  });

  describe('logging', () => {
    it('ticketIsNeverLogged', async () => {
      const spies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((name) =>
        vi.spyOn(console, name).mockImplementation(() => undefined),
      );
      const h = setup();
      const pair = signIn(h);
      h.svc.start();
      await settle();
      const socket = h.socket(0);
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      h.startMode = 'reject';
      socket.fireClose();
      await advance(1000);
      await advance(2000);

      const ticketStrings = [1, 2, 3].map((n) => FakeHubTicketRepository.ticketNumber(n));
      // The ticket appears in the connect URL only, once per attempt, and the access token in none.
      const urls = h.sockets.flatMap((s) => s.startedUrls);
      expect(urls.length).toBeGreaterThanOrEqual(2);
      for (const [index, url] of urls.entries()) {
        expect(url).toBe(`/hubs/episodes?ticket=${encodeURIComponent(ticketStrings[index] ?? '')}`);
        expect(url).not.toContain(pair.accessToken);
      }
      for (const spy of spies) {
        for (const call of spy.mock.calls) {
          const text = JSON.stringify(call);
          expect(text).not.toContain('ticket');
          for (const ticket of ticketStrings) expect(text).not.toContain(ticket);
        }
      }
    });
  });
});

// QA hardening (10.2.4-T1 gate): races, ladder edges and leak checks beyond the task's named cases.
describe('RealtimeService QA hardening', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    vi.setSystemTime(T0);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  describe('stale completions', () => {
    it('lateStart_AfterNewUserSignIn_ClosesTheOldSocketAndLeaksNothing', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'deferred';
      h.svc.start();
      await settle();
      const old = h.socket(0);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);

      signIn(h, 15 * MINUTE, 'user-2'); // another identity replaces the session while A's start is pending
      TestBed.tick();
      await settle();
      expect(h.sockets).toHaveLength(2);

      old.resolveStart(); // A's socket opens late
      await settle();
      expect(old.stopCalls).toBeGreaterThanOrEqual(1);
      expect(h.svc.state()).toBe('connecting');
      expect(h.svc.resync()).toBe(0);
      old.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 9 }));
      old.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      old.fireClose();
      await advance(60_000);
      expect(h.svc.progress().size).toBe(0);
      expect(handler).not.toHaveBeenCalled();
      expect(h.tickets.calls).toBe(2);

      h.socket(1).resolveStart();
      await settle();
      expect(h.svc.state()).toBe('connected');
      expect(h.svc.resync()).toBe(1);
    });

    it('lateToken_AfterStop_RequestsNoTicketAndOpensNoSocket', async () => {
      const h = setup();
      signIn(h, 0);
      h.sessionApi.mode = 'deferred';
      h.svc.start();
      await settle();
      expect(h.sessionApi.calls).toBe(1);
      h.svc.stop();
      h.sessionApi.resolveNext(tokens());
      await settle();
      await advance(60_000);
      expect(h.tickets.calls).toBe(0);
      expect(h.sockets).toHaveLength(0);
      expect(h.svc.state()).toBe('stopped');
      expect(h.svc.resync()).toBe(0);
    });

    it('lateTicketFailure_AfterStop_SchedulesNoBackoff', async () => {
      const h = setup();
      signIn(h);
      h.tickets.deferred = true;
      h.svc.start();
      await settle();
      h.svc.stop();
      h.tickets.rejectNext(FakeHubTicketRepository.unavailable());
      await settle();
      await advance(60_000);
      expect(h.tickets.calls).toBe(1);
      expect(h.svc.state()).toBe('stopped');
    });

    it('lateTicket_AfterPause_OpensNoSocket', async () => {
      const h = setup();
      signIn(h);
      h.tickets.deferred = true;
      h.svc.start();
      await settle();
      h.svc.pause();
      h.tickets.resolveNext();
      await settle();
      expect(h.sockets).toHaveLength(0);
      expect(h.svc.state()).toBe('paused');
      expect(h.svc.resync()).toBe(0);
    });

    it('lateStartRejection_AfterReset_SchedulesNoBackoff', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'deferred';
      TestBed.tick(); // the status effect starts the run now; a later first run would restart an idle service by itself
      h.svc.start();
      await settle();
      h.svc.reset();
      h.socket(0).rejectStart(new Error('aborted'));
      await settle();
      await advance(60_000);
      expect(h.tickets.calls).toBe(1);
      expect(h.svc.state()).toBe('idle');
    });

    it('resetThenImmediateStart_OldTicketNeverOpensASocket', async () => {
      const h = setup();
      signIn(h);
      h.tickets.deferred = true;
      h.svc.start();
      await settle();
      h.svc.reset();
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(2);
      h.tickets.resolveNext(); // the first run's ticket
      await settle();
      expect(h.sockets).toHaveLength(0);
      h.tickets.resolveNext();
      await settle();
      expect(h.sockets).toHaveLength(1);
      expect(h.socket(0).startedUrls[0]).toContain(FakeHubTicketRepository.ticketNumber(2));
      expect(h.svc.resync()).toBe(1);
    });

    it('closeOfAStoppedPausedOrResetSocket_SchedulesNothing', async () => {
      for (const end of ['stop', 'reset', 'pause'] as const) {
        TestBed.resetTestingModule();
        const h = setup();
        const socket = await connected(h);
        TestBed.tick();
        h.svc[end]();
        const state = h.svc.state();
        socket.fireClose(); // the library fires onclose for the stop() the service itself asked for
        await advance(60_000);
        expect(h.tickets.calls, end).toBe(1);
        expect(h.svc.state(), end).toBe(state);
      }
    });

    it('oldSocketAfterReconnect_EventsAndSecondCloseAreIgnored', async () => {
      const h = setup();
      const first = await connected(h);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);
      first.fireClose();
      await advance(1000);
      expect(h.svc.state()).toBe('connected');
      expect(h.sockets).toHaveLength(2);
      await advance(MIN_RESYNC_INTERVAL_MS + 250); // the (trailing) resync window of the reconnect
      handler.mockClear();

      first.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      first.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 5 }));
      first.fireClose();
      await advance(60_000);
      expect(handler).not.toHaveBeenCalled();
      expect(h.svc.progress().size).toBe(0);
      expect(h.svc.state()).toBe('connected');
      expect(h.tickets.calls).toBe(2);
    });

    it('doubleClose_FromTheSameSocket_SchedulesOneReconnect', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fireClose();
      socket.fireClose();
      await advance(1000);
      expect(h.tickets.calls).toBe(2);
      expect(h.sockets).toHaveLength(2);
    });

    it('closeDuringConnecting_IsIgnored_TheRejectingStartDrivesOneBackoff', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'deferred';
      h.svc.start();
      await settle();
      const socket = h.socket(0);
      socket.fireClose();
      h.startMode = 'ok';
      socket.rejectStart(new Error('closed during handshake'));
      await settle();
      expect(h.svc.state()).toBe('backoff');
      await advance(1000);
      expect(h.tickets.calls).toBe(2);
      expect(h.svc.state()).toBe('connected');
    });
  });

  describe('reconnect ladder edges', () => {
    it('startRejection_OnTheFirstConnect_IsAClose_NoTightLoop', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'reject';
      h.svc.start();
      await settle();
      await settle();
      expect(h.sockets).toHaveLength(1);
      expect(h.tickets.calls).toBe(1);
      expect(h.svc.state()).toBe('backoff');
      await advance(999);
      expect(h.tickets.calls).toBe(1);
      await advance(1);
      expect(h.tickets.calls).toBe(2);
      await advance(1999);
      expect(h.tickets.calls).toBe(2);
      await advance(1);
      expect(h.tickets.calls).toBe(3);
    });

    it('ticketUnauthorized_GetsOneRetryPerAttempt_NeverALoop', async () => {
      const h = setup();
      signIn(h);
      for (let i = 0; i < 4; i += 1) h.tickets.failures.push(FakeHubTicketRepository.unauthorized());
      h.svc.start();
      await settle();
      expect(h.tickets.calls).toBe(2);
      expect(h.svc.state()).toBe('backoff');
      await advance(1000);
      expect(h.tickets.calls).toBe(4);
      expect(h.svc.state()).toBe('backoff');
      await advance(1999);
      expect(h.tickets.calls).toBe(4);
      await advance(1);
      expect(h.tickets.calls).toBe(5);
      expect(h.svc.state()).toBe('connected');
    });

    it('rateLimited_RetryAfterSmallerThanTheLadder_KeepsTheLadderDelay', async () => {
      const h = setup();
      signIn(h);
      const unavailable = FakeHubTicketRepository.unavailable();
      h.tickets.failures.push(unavailable, unavailable, unavailable, FakeHubTicketRepository.rateLimited(1));
      h.svc.start();
      await settle();
      await advance(1000);
      await advance(2000);
      await advance(4000);
      expect(h.tickets.calls).toBe(4);
      expect(h.svc.state()).toBe('backoff');
      // Attempt 3 of the ladder is 8 s; a Retry-After of 1 s must not shorten it.
      await advance(7999);
      expect(h.tickets.calls).toBe(4);
      await advance(1);
      expect(h.tickets.calls).toBe(5);
    });

    it('rateLimited_RetryAfterBeyondTheCap_IsHonouredAndTheLadderContinuesAfterwards', async () => {
      const h = setup();
      signIn(h);
      h.tickets.failures.push(FakeHubTicketRepository.rateLimited(120), FakeHubTicketRepository.unavailable());
      h.svc.start();
      await settle();
      await advance(119_999);
      expect(h.tickets.calls).toBe(1);
      await advance(1);
      expect(h.tickets.calls).toBe(2); // second failure (503) uses ladder attempt 1, i.e. 2 s: the 429 did not reset it
      await advance(1999);
      expect(h.tickets.calls).toBe(2);
      await advance(1);
      expect(h.tickets.calls).toBe(3);
      expect(h.svc.state()).toBe('connected');
    });

    it('rateLimited_WithoutRetryAfter_UsesTheLadder', async () => {
      const h = setup();
      signIn(h);
      h.tickets.failures.push({ code: 'rate_limited', status: 429, kind: 'rate_limit', fieldErrors: {} });
      h.svc.start();
      await settle();
      await advance(999);
      expect(h.tickets.calls).toBe(1);
      await advance(1);
      expect(h.tickets.calls).toBe(2);
    });

    it('anEstablishedConnection_ResetsTheLadderToOneSecond', async () => {
      const h = setup();
      const socket = await connected(h);
      h.startMode = 'reject';
      socket.fireClose();
      await advance(1000); // fails → next delay 2 s
      h.startMode = 'ok';
      await advance(2000);
      expect(h.svc.state()).toBe('connected');
      await advance(STABLE_RESET_MS); // the ladder restarts only after a stable link
      h.socket(h.sockets.length - 1).fireClose();
      const calls = h.tickets.calls;
      await advance(999);
      expect(h.tickets.calls).toBe(calls);
      await advance(1);
      expect(h.tickets.calls).toBe(calls + 1);
    });

    it('pauseDuringBackoff_CancelsTheTimer_ResumeConnectsOnce', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fireClose();
      expect(h.svc.state()).toBe('backoff');
      h.svc.pause();
      await advance(60_000);
      expect(h.tickets.calls).toBe(1);
      expect(h.svc.state()).toBe('paused');
      h.svc.resume();
      await settle();
      expect(h.tickets.calls).toBe(2);
      expect(h.svc.state()).toBe('connected');
    });

    it('jitterOnTopOfTheCap_ReachesThirtySixSeconds_DocumentedDivergence', async () => {
      const h = setup(() => 0.999999);
      const socket = await connected(h);
      h.startMode = 'reject';
      socket.fireClose();
      // Walk to attempt 5 (base 30 s); every step carries its own ~20 % jitter.
      for (const base of [1000, 2000, 4000, 8000, 16_000]) await advance(Math.ceil(base * 1.2));
      const calls = h.tickets.calls;
      await advance(30_000);
      expect(h.tickets.calls).toBe(calls); // not yet at 30 s …
      await advance(5989);
      expect(h.tickets.calls).toBe(calls); // … nor at 35.989 s
      await advance(11);
      expect(h.tickets.calls).toBe(calls + 1); // the attempt fires at ~36 s: the 30 s cap plus up to 20 % jitter
    });
  });

  describe('visibility and lifecycle', () => {
    it('visibilityVisible_WhilePaused_EmitsNothing_AfterResumeTheListenerStillWorksOnce', async () => {
      const h = setup();
      const socket = await connected(h);
      vi.setSystemTime(T0 + 14 * DAY);
      socket.fireClose();
      expect(h.svc.state()).toBe('paused');
      setVisibility('visible');
      const paused = h.svc.resync();
      document.dispatchEvent(new Event('visibilitychange'));
      expect(h.svc.resync()).toBe(paused);
      expect(h.tickets.calls).toBe(1);

      h.svc.resume();
      await settle();
      const connectedAt = h.svc.resync();
      document.dispatchEvent(new Event('visibilitychange'));
      expect(h.svc.resync()).toBe(connectedAt + 1);
    });

    it('visibilityVisible_WhileOffline_BackingOff_EmitsNothing', async () => {
      const h = setup();
      const socket = await connected(h);
      h.startMode = 'reject';
      socket.fireClose();
      await advance(1000);
      expect(h.svc.state()).toBe('backoff');
      setVisibility('visible');
      const before = h.svc.resync();
      document.dispatchEvent(new Event('visibilitychange'));
      expect(h.svc.resync()).toBe(before);
      expect(h.sockets).toHaveLength(2);
    });

    it('resetThenStart_AttachesTheVisibilityListenerExactlyOnce', async () => {
      const h = setup();
      await connected(h);
      h.svc.reset();
      h.svc.start();
      await settle();
      expect(h.svc.state()).toBe('connected');
      setVisibility('visible');
      const before = h.svc.resync();
      document.dispatchEvent(new Event('visibilitychange'));
      expect(h.svc.resync()).toBe(before + 1);
    });

    it('stopThenExplicitStart_ReconnectsWithANewTicket', async () => {
      const h = setup();
      await connected(h);
      h.svc.stop();
      h.svc.start();
      await settle();
      expect(h.svc.state()).toBe('connected');
      expect(h.tickets.calls).toBe(2);
      expect(h.sockets).toHaveLength(2);
    });

    it('enrolmentAfterASession_ResetsTheRun', async () => {
      const h = setup();
      const socket = await connected(h);
      h.store.beginEnrolment('enrol-token');
      TestBed.tick();
      await settle();
      expect(h.store.status()).not.toBe('authenticated');
      expect(h.svc.state()).toBe('idle');
      expect(socket.stopCalls).toBeGreaterThanOrEqual(1);
      await advance(60_000);
      expect(h.tickets.calls).toBe(1);
    });
  });

  describe('subscribeEpisode and progress', () => {
    it('subscribeEpisode_NotifiesOnlyMatchingKeys_SeriesIdAndEpisodeIdIncluded', async () => {
      const h = setup();
      const socket = await connected(h);
      const ep1 = vi.fn();
      const ep2 = vi.fn();
      const series = vi.fn();
      h.svc.subscribeEpisode('ep-1', ep1);
      h.svc.subscribeEpisode('ep-2', ep2);
      h.svc.subscribeEpisode('series-1', series);

      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1', seriesId: 'series-1' }));
      await advance(250);
      expect([ep1.mock.calls.length, ep2.mock.calls.length, series.mock.calls.length]).toEqual([1, 0, 1]);

      // Ids that match nobody, or that are not episode/series ids, open no window.
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-9' }));
      socket.fire('EpisodeUpdated', envelope('episode_updated', { renderJobId: 'ep-1' }));
      socket.fire('EpisodeUpdated', envelope('episode_updated', {}));
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 42 as unknown as string }));
      socket.fire('EpisodeUpdated', { userId: 'u', event: 'episode_updated' });
      await advance(250);
      expect([ep1.mock.calls.length, ep2.mock.calls.length, series.mock.calls.length]).toEqual([1, 0, 1]);

      // Other mapped events about the episode invalidate too.
      socket.fire('PublicationUpdated', envelope('publication_updated', { episodeId: 'ep-2' }));
      await advance(250);
      expect(ep2).toHaveBeenCalledTimes(1);
    });

    it('renderProgress_NeverNotifiesSubscribers', async () => {
      const h = setup();
      const socket = await connected(h);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1', seriesId: 's' }, { stage: 'voice', percent: 5 }));
      await advance(1000);
      expect(handler).not.toHaveBeenCalled();
    });

    it('unsubscribe_BeforeTheEvent_AndRefCounting', async () => {
      const h = setup();
      const socket = await connected(h);
      const a = vi.fn();
      const b = vi.fn();
      const subA = h.svc.subscribeEpisode('ep-1', a);
      h.svc.subscribeEpisode('ep-1', b);
      subA.unsubscribe();
      subA.unsubscribe(); // idempotent
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(250);
      expect(a).not.toHaveBeenCalled();
      expect(b).toHaveBeenCalledTimes(1);
    });

    it('unsubscribe_BetweenCloseAndResync_SkipsTheHandler_LiveOnesResyncOnce', async () => {
      const h = setup();
      const socket = await connected(h);
      const gone = vi.fn();
      const live = vi.fn();
      const sub = h.svc.subscribeEpisode('ep-1', gone);
      h.svc.subscribeEpisode('ep-2', live);
      socket.fireClose();
      sub.unsubscribe();
      await advance(1000);
      await advance(MIN_RESYNC_INTERVAL_MS); // a reconnect inside the resync spacing resyncs once the spacing passed
      await advance(250);
      expect(gone).not.toHaveBeenCalled();
      expect(live).toHaveBeenCalledTimes(1);
    });

    it('aThrowingSubscriberDoesNotBreakTheServiceOrItsSiblings', async () => {
      const h = setup();
      const socket = await connected(h);
      const sibling = vi.fn();
      h.svc.subscribeEpisode('ep-1', () => {
        throw new Error('refetch exploded');
      });
      h.svc.subscribeEpisode('ep-1', sibling);
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(250);
      expect(sibling).toHaveBeenCalledTimes(1);
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await advance(250);
      expect(sibling).toHaveBeenCalledTimes(2);
      expect(h.svc.state()).toBe('connected');
    });

    it('progress_NeverMutatesAPublishedMap_AndResetSwapsInAFreshOne', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 10 }));
      await settle();
      const first = h.svc.progress();
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 20 }));
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-2' }, { stage: 'images', percent: 1 }));
      await settle();
      const third = h.svc.progress();
      expect(first.size).toBe(1);
      expect(first.get('ep-1')?.percent).toBe(10);
      expect(third.size).toBe(2);
      expect(third.get('ep-1')?.percent).toBe(20);

      h.svc.reset();
      expect(third.size).toBe(2); // the old snapshot is untouched: nobody cleared it in place
      expect(h.svc.progress().size).toBe(0);
      expect(h.svc.progress()).not.toBe(third);
    });
  });

  describe('secrets', () => {
    function reachableStrings(root: object, skip: ReadonlySet<string>): { names: string[]; strings: string[] } {
      const seen = new WeakSet<object>();
      const strings: string[] = [];
      const visit = (value: unknown, depth: number): void => {
        if (isSignal(value)) {
          visit(value(), depth + 1);
          return;
        }
        if (typeof value === 'string') strings.push(value);
        if (typeof value !== 'object' || value === null || depth > 8 || seen.has(value)) return;
        seen.add(value);
        if (value instanceof Map) {
          for (const [k, v] of value) {
            visit(k, depth + 1);
            visit(v, depth + 1);
          }
          return;
        }
        if (value instanceof Set || Array.isArray(value)) {
          for (const v of value) visit(v, depth + 1);
          return;
        }
        for (const name of Object.getOwnPropertyNames(value)) {
          strings.push(name);
          visit((value as Record<string, unknown>)[name], depth + 1);
        }
      };
      const names = Object.getOwnPropertyNames(root);
      for (const name of names) if (!skip.has(name)) visit((root as Record<string, unknown>)[name], 0);
      return { names, strings };
    }

    it('theTicketAndTheAccessTokenAreOnNoServiceField', async () => {
      const h = setup();
      const pair = signIn(h);
      h.tickets.failures.push(FakeHubTicketRepository.unavailable());
      h.svc.start();
      await settle();
      await advance(1000);
      const socket = h.socket(0);
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      h.svc.subscribeEpisode('ep-1', () => undefined);
      h.svc.on('EpisodeUpdated').subscribe(() => undefined);
      socket.fireClose();
      await advance(2000); // the ladder did not reset: the second connect step is 2 s
      expect(h.svc.state()).toBe('connected');

      // The injected collaborators and the live port are other objects; the fake port records URLs on purpose.
      const skip = new Set(['tickets', 'connectionFactory', 'session', 'activity', 'mapper', 'random', 'document', 'connection']);
      const { names, strings } = reachableStrings(h.svc, skip);
      expect(names).toContain('subscriptions');
      expect(names).toContain('backoffTimer');
      const secrets = [pair.accessToken, ...[1, 2, 3].map((n) => FakeHubTicketRepository.ticketNumber(n))];
      for (const text of strings) {
        for (const secret of secrets) expect(text).not.toContain(secret);
        expect(text).not.toContain('ticket=');
      }
      // No field is named after the URL, the ticket or a token (the collaborator `tickets` is the repository port).
      expect(names.filter((name) => /url|token|^ticket$/i.test(name))).toEqual([]);
    });

    it('unknownEventsAndBadPayloadsLogNothing', async () => {
      const spies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((name) =>
        vi.spyOn(console, name).mockImplementation(() => undefined),
      );
      const h = setup();
      const socket = await connected(h);
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-secret-id' }, { stage: 'nope', percent: 1 }));
      socket.fire('EpisodeUpdated', null);
      socket.fire('EpisodeUpdated', 'ep-secret-id');
      socket.fire('SomethingNew', envelope('x', { episodeId: 'ep-secret-id' }));
      for (const spy of spies) expect(spy).not.toHaveBeenCalled();
    });
  });
});

@Component({
  selector: 'nts-realtime-probe',
  template: '<p id="state">{{ svc.state() }}</p><p id="pct">{{ svc.progress().get("ep-1")?.percent ?? "none" }}</p>',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class ProbeComponent {
  readonly svc = inject(RealtimeService);
}

describe('RealtimeService in a zoneless host', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('socketUpdate_RendersAfterWhenStable_WithoutForcedChangeDetection', async () => {
    const h = setup();
    const fixture = TestBed.createComponent(ProbeComponent);
    signIn(h);
    h.svc.start();
    await fixture.whenStable();
    const text = (id: string): string => (fixture.nativeElement as HTMLElement).querySelector(id)?.textContent ?? '';
    expect(text('#state')).toBe('connected');
    expect(text('#pct')).toBe('none');

    h.socket(0).fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 42 }));
    await Promise.resolve(); // the progress burst is published in one microtask
    await fixture.whenStable();
    expect(text('#pct')).toBe('42');
  });
});

describe('RealtimeService review fixes (10.2.4-T1)', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    vi.setSystemTime(T0);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  describe('CR-1 / SEC-2: generation fencing', () => {
    it('connectedSocketOfA_AfterIdentityBWithoutEnd_DropsEventsAndItsCloseSchedulesNothing', async () => {
      const h = setup();
      const a = await connected(h);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);

      signIn(h, 15 * MINUTE, 'user-2'); // B replaces A; the status effect has not run yet
      a.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 9 }));
      a.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      await settle();
      expect(h.svc.progress().size).toBe(0);
      expect(a.stopCalls).toBeGreaterThanOrEqual(1);

      a.fireClose();
      await settle();
      expect(h.svc.state()).not.toBe('backoff');
      expect(h.tickets.calls).toBe(1);

      TestBed.tick(); // the effect now resets A's run and starts B's
      await settle();
      expect(h.svc.state()).toBe('connected');
      expect(h.tickets.calls).toBe(2);
      await advance(60_000);
      expect(handler).not.toHaveBeenCalled();
      expect(h.svc.progress().size).toBe(0);
    });

    it('bareStopOfA_ThenIdentityBSignsIn_ProgressOfAIsGone', async () => {
      const h = setup();
      const a = await connected(h);
      a.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 9 }));
      await settle();
      expect(h.svc.progress().size).toBe(1);

      h.svc.stop();
      expect(h.svc.state()).toBe('stopped');
      expect(h.svc.progress().size).toBe(1); // a bare stop keeps it

      signIn(h, 15 * MINUTE, 'user-2');
      TestBed.tick();
      await settle();
      expect(h.svc.progress().size).toBe(0);
      expect(h.svc.state()).toBe('connected');
    });
  });

  describe('CR-2: close while connecting', () => {
    it('closeBeforeStartResolves_IsOneBackoffAndOneNewTicket_NeverStuckConnected', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'deferred';
      h.svc.start();
      await settle();
      const first = h.socket(0);
      first.fireClose();
      first.resolveStart();
      await settle();
      expect(h.svc.state()).toBe('backoff');
      expect(h.svc.resync()).toBe(0);
      expect(h.tickets.calls).toBe(1);

      h.startMode = 'ok';
      await advance(999);
      expect(h.tickets.calls).toBe(1);
      await advance(1);
      expect(h.tickets.calls).toBe(2);
      expect(h.svc.state()).toBe('connected');
      expect(h.sockets).toHaveLength(2);
    });

    it('closeBeforeStartRejects_IsStillExactlyOneBackoffStep', async () => {
      const h = setup();
      signIn(h);
      h.startMode = 'deferred';
      h.svc.start();
      await settle();
      const first = h.socket(0);
      first.fireClose();
      first.rejectStart(new Error('upgrade failed'));
      await settle();
      expect(h.svc.state()).toBe('backoff');

      h.startMode = 'ok';
      await advance(1000); // step 0 (1 s): a double step would have moved the next delay to 4 s
      expect(h.svc.state()).toBe('connected');
      h.socket(1).fireClose();
      await advance(1999);
      expect(h.tickets.calls).toBe(2);
      await advance(1);
      expect(h.tickets.calls).toBe(3);
    });
  });

  describe('PERF-1: pause cancels coalescing windows', () => {
    it('pause_CancelsOpenWindows_KeepsRegistrations_ResumeResyncsThem', async () => {
      const h = setup();
      const socket = await connected(h);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      h.svc.pause();
      await advance(COALESCE_WINDOW_MS * 4);
      expect(handler).not.toHaveBeenCalled();

      h.svc.resume();
      await settle();
      await advance(COALESCE_WINDOW_MS);
      expect(handler).toHaveBeenCalledTimes(1);
    });
  });

  describe('SEC-1: stable ladder and bounded resync', () => {
    it('acceptThenClose_FiveTimes_ClimbsTheLadder_TicketsAndResyncsStayBounded', async () => {
      const h = setup();
      let socket = await connected(h);
      for (const delay of [1000, 2000, 4000, 8000, 16_000]) {
        const calls = h.tickets.calls;
        socket.fireClose();
        await advance(delay - 1);
        expect(h.tickets.calls, `before ${String(delay)}`).toBe(calls);
        await advance(1);
        expect(h.tickets.calls, `at ${String(delay)}`).toBe(calls + 1);
        expect(h.svc.state()).toBe('connected');
        socket = h.socket(h.sockets.length - 1);
      }
      expect(h.tickets.calls).toBe(6);
      // t = 0, 1, 3, 7, 15, 31 s: only the connects at 0, 7, 15 and 31 s are ≥ 5 s apart from the last emitted one.
      expect(h.svc.resync()).toBe(4);
    });

    it('aSuppressedReconnectResync_TrailsOnceWhenTheLinkStaysUp', async () => {
      const h = setup();
      const socket = await connected(h);
      expect(h.svc.resync()).toBe(1);
      socket.fireClose();
      await advance(1000);
      expect(h.svc.state()).toBe('connected');
      expect(h.svc.resync()).toBe(1);
      await advance(MIN_RESYNC_INTERVAL_MS - 1000 - 1);
      expect(h.svc.resync()).toBe(1);
      await advance(1);
      expect(h.svc.resync()).toBe(2);
      await advance(MIN_RESYNC_INTERVAL_MS * 3);
      expect(h.svc.resync()).toBe(2);
    });

    it('aCloseCancelsTheTrailingResync', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fireClose();
      await advance(1000);
      h.socket(1).fireClose(); // the reconnect dropped again before its trailing resync was due
      expect(h.svc.resync()).toBe(1);
      await advance(MIN_RESYNC_INTERVAL_MS - 1000 - 1); // 2 s backoff still running: no trailing resync fired
      expect(h.svc.resync()).toBe(1);
    });

    it('stableForThirtySeconds_ThenClose_RestartsTheLadderAtOneSecond', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fireClose();
      await advance(1000);
      h.socket(1).fireClose();
      await advance(2000); // attempt counter is now 2 (next 4 s)
      expect(h.svc.state()).toBe('connected');
      await advance(STABLE_RESET_MS);
      h.socket(2).fireClose();
      const calls = h.tickets.calls;
      await advance(999);
      expect(h.tickets.calls).toBe(calls);
      await advance(1);
      expect(h.tickets.calls).toBe(calls + 1);
    });

    it('aCloseBeforeTheStableWindowEnds_CancelsTheResetTimer', async () => {
      const h = setup();
      const socket = await connected(h);
      await advance(STABLE_RESET_MS - 1);
      socket.fireClose();
      await Promise.resolve();
      await advance(1000);
      h.socket(1).fireClose();
      await advance(1999);
      expect(h.tickets.calls).toBe(2); // 1 s then 2 s: no reset happened at the old window's end
      await advance(1);
      expect(h.tickets.calls).toBe(3);
    });

    it('stopResetAndPause_ClearTheStableAndResyncTimers', async () => {
      for (const end of ['stop', 'reset', 'pause'] as const) {
        TestBed.resetTestingModule();
        const h = setup();
        await connected(h);
        const withTimers = vi.getTimerCount();
        h.svc[end]();
        expect(vi.getTimerCount(), end).toBeLessThan(withTimers); // at least the stable-reset timer is gone
      }
    });
  });

  describe('PERF-2 / SEC-3: progress batching, cap and id length', () => {
    function progressEvent(episodeId: string, percent: number): unknown {
      return envelope('render_progress', { episodeId }, { stage: 'voice', percent });
    }

    it('burst_PublishesOneNewMapWithTheLastValues', async () => {
      const h = setup();
      const socket = await connected(h);
      const before = h.svc.progress();
      for (let i = 0; i < 50; i += 1) {
        socket.fire('RenderProgress', progressEvent('ep-1', i));
        socket.fire('RenderProgress', progressEvent('ep-2', 100 - i));
      }
      expect(h.svc.progress()).toBe(before); // nothing is copied or published per event
      await Promise.resolve();
      const published = h.svc.progress();
      expect(published).not.toBe(before);
      expect(published.get('ep-1')?.percent).toBe(49);
      expect(published.get('ep-2')?.percent).toBe(51);
      expect(before.size).toBe(0);
      await settle();
      expect(h.svc.progress()).toBe(published); // one publish for the whole burst
    });

    it('burstEvents_StillEmitOnTheEventStreamOneByOne', async () => {
      const h = setup();
      const socket = await connected(h);
      const seen: number[] = [];
      h.svc.on('RenderProgress').subscribe((event) => seen.push(event.progress.percent));
      for (let i = 0; i < 5; i += 1) socket.fire('RenderProgress', progressEvent('ep-1', i));
      expect(seen).toEqual([0, 1, 2, 3, 4]);
    });

    it('theMapIsCapped_TheOldestEntryIsEvictedFirst', async () => {
      const h = setup();
      const socket = await connected(h);
      for (let i = 0; i < MAX_PROGRESS_ENTRIES + 10; i += 1) socket.fire('RenderProgress', progressEvent(`ep-${String(i)}`, 1));
      await settle();
      let map = h.svc.progress();
      expect(map.size).toBe(MAX_PROGRESS_ENTRIES);
      expect(map.has('ep-0')).toBe(false);
      expect(map.has('ep-9')).toBe(false);
      expect(map.has('ep-10')).toBe(true);
      expect(map.has(`ep-${String(MAX_PROGRESS_ENTRIES + 9)}`)).toBe(true);

      // An update refreshes recency: ep-10 survives the next eviction, ep-11 does not.
      socket.fire('RenderProgress', progressEvent('ep-10', 2));
      socket.fire('RenderProgress', progressEvent('new', 3));
      await settle();
      map = h.svc.progress();
      expect(map.size).toBe(MAX_PROGRESS_ENTRIES);
      expect(map.get('ep-10')?.percent).toBe(2);
      expect(map.has('ep-11')).toBe(false);
    });

    it('resetBeforeTheMicrotask_DiscardsTheBurst', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fire('RenderProgress', progressEvent('ep-1', 5));
      h.svc.reset();
      await settle();
      expect(h.svc.progress().size).toBe(0);
      // …and the next burst after the reset publishes normally.
      h.svc.start();
      await settle();
      h.socket(1).fire('RenderProgress', progressEvent('ep-2', 6));
      await settle();
      expect(h.svc.progress().get('ep-2')?.percent).toBe(6);
    });

    it('anIdOfMoreThan64Characters_IsDropped_64IsKept', async () => {
      const h = setup();
      const socket = await connected(h);
      const seen: RealtimeEvent[] = [];
      h.svc.on('EpisodeUpdated').subscribe((event) => seen.push(event));
      socket.fire('RenderProgress', progressEvent('x'.repeat(65), 1));
      socket.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'y'.repeat(65) }));
      socket.fire('RenderProgress', progressEvent('z'.repeat(64), 2));
      await settle();
      const map = h.svc.progress();
      expect([...map.keys()]).toEqual(['z'.repeat(64)]);
      expect(seen).toHaveLength(1);
      expect(seen[0]?.ids.episodeId).toBeUndefined();
    });
  });

  describe('DELTA fixes: stale sockets, pending progress, timer fencing', () => {
    it('DELTA-1 replacedSocket_EventAndClose_StopsThatSocketOnly_DispatchesAndSchedulesNothing', async () => {
      const h = setup();
      const old = await connected(h);
      old.fireClose();
      await advance(1000);
      expect(h.svc.state()).toBe('connected');
      const current = h.socket(1);
      const handler = vi.fn();
      h.svc.subscribeEpisode('ep-1', handler);
      await advance(MIN_RESYNC_INTERVAL_MS + COALESCE_WINDOW_MS);
      handler.mockClear();
      const tickets = h.tickets.calls;
      const resync = h.svc.resync();
      const oldStops = old.stopCalls;

      old.fire('EpisodeUpdated', envelope('episode_updated', { episodeId: 'ep-1' }));
      old.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 9 }));
      expect(old.stopCalls).toBeGreaterThan(oldStops);
      const afterEvents = old.stopCalls;
      old.fireClose();
      expect(old.stopCalls).toBeGreaterThan(afterEvents);
      await advance(60_000);

      expect(handler).not.toHaveBeenCalled();
      expect(h.svc.progress().size).toBe(0);
      expect(h.svc.resync()).toBe(resync);
      expect(h.tickets.calls).toBe(tickets);
      expect(h.sockets).toHaveLength(2);
      expect(current.stopCalls).toBe(0);
      expect(h.svc.state()).toBe('connected');
    });

    it.each(['stop', 'pause'] as const)('DELTA-2 progressQueuedBefore_%s_NeverPublishes', async (end) => {
      const h = setup();
      const socket = await connected(h);
      const before = h.svc.progress();
      socket.fire('RenderProgress', envelope('render_progress', { episodeId: 'ep-1' }, { stage: 'voice', percent: 9 }));
      h.svc[end]();
      await settle();
      expect(h.svc.progress()).toBe(before);
      expect(h.svc.progress().size).toBe(0);
    });

    it('DELTA-3 trailingResyncTimer_AfterIdentityChange_EmitsNoResync', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fireClose();
      await advance(1000);
      expect(h.svc.state()).toBe('connected');
      const resync = h.svc.resync();

      // Timers fire synchronously, before the status effect of the new identity can run.
      signIn(h, 15 * MINUTE, 'user-2');
      vi.advanceTimersByTime(MIN_RESYNC_INTERVAL_MS);
      expect(h.svc.resync()).toBe(resync);
    });

    it('DELTA-3 stableResetTimer_AfterIdentityChange_KeepsTheLadder', async () => {
      const h = setup();
      const socket = await connected(h);
      socket.fireClose();
      await advance(1000);
      expect(h.svc.state()).toBe('connected');
      const internals = h.svc as unknown as { attempt: number };
      expect(internals.attempt).toBe(1);

      // Only the generation moves (no signal change), so the status effect cannot restart the run before the timer.
      vi.spyOn(h.store, 'generation').mockReturnValue(h.store.generation() + 1);
      vi.advanceTimersByTime(STABLE_RESET_MS);
      expect(internals.attempt).toBe(1);
    });
  });
});
