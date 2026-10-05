import type { Page, Request, WebSocketRoute } from '@playwright/test';
import type { EpisodeDetailDto } from '../../src/app/shared/api/models/episode-detail-dto';
import { expect } from './csp.fixture';
import { fakeAccessToken, fulfilJson, problem, SERIES_ID, seriesDetail, stubSeriesApi, stubSession, type E2eLang, type E2eTopic } from './series-stubs';

export { SERIES_ID } from './series-stubs';
export const EPISODE_ID = '33333333-3333-4333-8333-333333333333';
export const NEW_EPISODE_PATH = `/series/${SERIES_ID}/episodes/new`;
const RUN_ID = '44444444-4444-4444-8444-444444444444';
const TICKET = 'e2e-hub-ticket-not-for-storage';
const RECORD_SEPARATOR = '\u001e';

export interface RequestEntry {
  readonly order: number;
  readonly kind: 'series' | 'create' | 'generate' | 'detail' | 'ticket' | 'websocket' | 'event' | 'close';
  readonly method: string;
  /** Query-free URL: never retains a ticket, access token, user content or body. */
  readonly url: string;
  readonly idempotencyKey: string | null;
}

type GenerateReply = 'accepted' | 'abort' | { readonly status: number; readonly code: string; readonly retryAfter?: number };
export interface EpisodeStubOptions {
  readonly lang?: E2eLang;
  readonly topic?: E2eTopic;
  readonly createAbort?: boolean;
  readonly details?: readonly EpisodeDetailDto[];
  readonly generateReplies?: readonly GenerateReply[];
}

export function episodeDetail(stage: NonNullable<EpisodeDetailDto['generation']>['stage'] = 'queued', overrides: Partial<EpisodeDetailDto> = {}): EpisodeDetailDto {
  return {
    id: EPISODE_ID, seriesId: SERIES_ID, title: null, brief: 'Stubbed detail, not generated story text',
    lengthTier: 'standard', status: 'story_generating', etag: `"episode-${stage}"`, nextActions: [],
    currentStory: null, finalVideo: null, renderJob: null, publications: null,
    generation: { kind: 'story', status: stage === 'queued' ? 'queued' : 'running', stage, errorCode: null, startedAt: '2026-10-05T12:00:00Z' },
    ...overrides,
  };
}

export interface EpisodeStubs {
  readonly log: RequestEntry[];
  readonly connected: () => number;
  readonly entries: (kind: RequestEntry['kind']) => RequestEntry[];
  /** Change the GET response before pushing an invalidation; extra GETs do not silently consume the script. */
  readonly advance: () => void;
  readonly push: (ids?: { readonly episodeId?: string; readonly seriesId?: string }, extra?: Readonly<Record<string, unknown>>) => void;
  readonly close: () => void;
}

/** Playwright 1.63 (package-lock): routeWebSocket supports a real SignalR JSON handshake, no negotiate/server. */
export async function stubEpisodes(page: Page, options: EpisodeStubOptions = {}): Promise<EpisodeStubs> {
  const log: RequestEntry[] = [];
  const record = (kind: RequestEntry['kind'], method: string, url: string, key: string | null = null): void => {
    log.push({ order: log.length, kind, method, url: new URL(url, 'http://stub.test').origin + new URL(url, 'http://stub.test').pathname, idempotencyKey: key });
  };
  const recordRequest = (kind: RequestEntry['kind'], request: Request): void => record(kind, request.method(), request.url(), request.headers()['idempotency-key'] ?? null);
  await stubSession(page, options.lang ?? 'vi');
  await stubSeriesApi(page, { detail: seriesDetail({ id: SERIES_ID, topic: options.topic ?? 'ghost' }) });
  // A last-registered wrapper keeps the shared series fixture authoritative and records ordering without bodies.
  await page.route(`**/api/v1/series/${SERIES_ID}`, async (route) => {
    recordRequest('series', route.request());
    await route.fallback();
  });
  const details = options.details ?? [episodeDetail()];
  let detailIndex = 0;
  let generateIndex = 0;
  let connections = 0;
  let socket: WebSocketRoute | null = null;
  let handshaken = false;
  await page.route('**/api/v1/hubs/ticket', async (route) => {
    recordRequest('ticket', route.request());
    await fulfilJson(route, 200, { ticket: TICKET, expiresAt: new Date(Date.now() + 60_000).toISOString() });
  });
  await page.routeWebSocket(/\/hubs\/episodes(?:\?|$)/, (ws) => {
    socket = ws;
    handshaken = false;
    ws.onMessage((message) => {
      if (typeof message !== 'string') return;
      for (const frame of message.split(RECORD_SEPARATOR).filter(Boolean)) {
        const data: unknown = JSON.parse(frame);
        if (typeof data !== 'object' || data === null) continue;
        if ('protocol' in data && data.protocol === 'json') {
          ws.send(`{}${RECORD_SEPARATOR}`);
          handshaken = true;
          connections += 1;
          record('websocket', 'WS', ws.url());
        } else if ('type' in data && data.type === 6) {
          ws.send(`${JSON.stringify({ type: 6 })}${RECORD_SEPARATOR}`);
        }
      }
    });
    ws.onClose(() => {
      if (socket === ws) { socket = null; handshaken = false; }
    });
  });
  await page.route(`**/api/v1/series/${SERIES_ID}/episodes`, async (route) => {
    recordRequest('create', route.request());
    if (options.createAbort) return route.abort('failed');
    // Actual API contract is a JSON UUID scalar, not { id }, with Location and ETag.
    await fulfilJson(route, 201, EPISODE_ID, { Location: `/api/v1/episodes/${EPISODE_ID}`, ETag: '"created"' });
  });
  await page.route(`**/api/v1/episodes/${EPISODE_ID}/story/generate`, async (route) => {
    recordRequest('generate', route.request());
    const replies = options.generateReplies ?? ['accepted'];
    const reply = replies[Math.min(generateIndex++, replies.length - 1)] ?? 'accepted';
    if (reply === 'abort') return route.abort('failed');
    if (typeof reply === 'object') {
      return fulfilJson(route, reply.status, problem(reply.status, reply.code), {
        'Content-Type': 'application/problem+json', ...(reply.retryAfter === undefined ? {} : { 'Retry-After': String(reply.retryAfter) }),
      });
    }
    await fulfilJson(route, 202, { generationRunId: RUN_ID, status: 'queued', stage: 'queued' }, { Location: `/api/v1/generation-runs/${RUN_ID}` });
  });
  await page.route(`**/api/v1/episodes/${EPISODE_ID}`, async (route) => {
    recordRequest('detail', route.request());
    const detail = details[Math.min(detailIndex, details.length - 1)] ?? episodeDetail();
    await fulfilJson(route, 200, detail, { ETag: detail.etag });
  });
  return {
    log, connected: () => connections, entries: (kind) => log.filter((entry) => entry.kind === kind),
    advance: () => { detailIndex += 1; },
    push: (ids = { episodeId: EPISODE_ID, seriesId: SERIES_ID }, extra = {}) => {
      if (socket === null || !handshaken) throw new Error('SignalR handshake has not completed');
      record('event', 'EVENT', '/hubs/episodes');
      // RealtimeService parses payload.ids and ignores generation/data: ids-only is the canonical invalidation.
      socket.send(`${JSON.stringify({ type: 1, target: 'EpisodeUpdated', arguments: [{ ...extra, ids }] })}${RECORD_SEPARATOR}`);
    },
    close: () => {
      if (socket === null || !handshaken) throw new Error('No connected SignalR socket to close');
      record('close', 'CLOSE', '/hubs/episodes');
      socket.close({ code: 1011, reason: 'E2E server-side disconnect' });
      socket = null;
      handshaken = false;
    },
  };
}

/** Tracks only leak flags, never console text/URLs/storage values: test output cannot leak a secret itself. */
export function watchPrivacy(page: Page, brief: string): () => Promise<void> {
  const secrets = [brief, TICKET, fakeAccessToken()];
  let consoleLeak = false;
  let requestLeak = false;
  page.on('console', (message) => {
    const text = message.text();
    // Chromium owns failed-upgrade diagnostics and includes the WS URL; the app cannot suppress that line.
    if (message.type() === 'error' && /^WebSocket connection to .* failed/.test(text)) return;
    consoleLeak ||= secrets.some((secret) => text.includes(secret));
  });
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.pathname !== '/hubs/episodes') requestLeak ||= secrets.some((secret) => request.url().includes(secret));
  });
  return async () => {
    const storageLeak = await page.evaluate((values) => [localStorage, sessionStorage].some((storage) =>
      Object.keys(storage).some((key) => values.some((value) => key.includes(value) || (storage.getItem(key) ?? '').includes(value))),
    ), secrets);
    expect(consoleLeak, 'user content or credentials leaked to console').toBe(false);
    expect(requestLeak, 'user content or credentials leaked outside the hub URL').toBe(false);
    expect(storageLeak, 'user content or credentials persisted in browser storage').toBe(false);
  };
}

/** Deliberately bounded quiet window: detects polling/duplicate refetches instead of hiding them behind a script. */
export async function expectNoExtraDetails(page: Page, stubs: EpisodeStubs, count: number): Promise<void> {
  await page.waitForTimeout(650);
  expect(stubs.entries('detail')).toHaveLength(count);
}
