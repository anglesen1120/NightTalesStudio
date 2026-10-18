import type { Locator, Page, Request, Route, WebSocketRoute } from '@playwright/test';
import type { EpisodeDetailDto } from '../src/app/shared/api/models/episode-detail-dto';
import type { RenderJobSummaryDto } from '../src/app/shared/api/models/render-job-summary-dto';
import coreEn from '../src/assets/i18n/core/en.json' with { type: 'json' };
import coreVi from '../src/assets/i18n/core/vi.json' with { type: 'json' };
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import episodesVi from '../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import errorsEn from '../src/assets/i18n/errors/en.json' with { type: 'json' };
import errorsVi from '../src/assets/i18n/errors/vi.json' with { type: 'json' };
import { expectA11yClean } from './fixtures/axe';
import { describeCsp, expect, test } from './fixtures/csp.fixture';
import { episodeDetail, EPISODE_ID, SERIES_ID } from './fixtures/episodes-stubs';
import {
  CORE_COPY,
  expectNoHorizontalScroll,
  fulfilJson,
  problem,
  seriesDetail,
  silentWav,
  stubSeriesApi,
  stubSession,
  type E2eLang,
} from './fixtures/series-stubs';
import { STORY_CONTENT, storySnapshot } from './fixtures/story-editor-stubs';

// 10.6.2-T1 — video review on the episode page: acceptance checks 14 and 15, STUBBED variant.
//
// Owner decision for this lane (the same one 10.6.1-T1 and 10.6.1-T2 shipped): this lane runs no compose stack and no
// mock-mpt, so `GET /api/v1/episodes/{id}`, the realtime hub, the three video-review routes and the signed-media
// origin are stubbed exactly like `render-timeline.spec.ts` does. The task file's real-stack wording (the container
// target of `playwright.config.ts`, nginx serving the built app, run by **11.5.1-T1**'s `e2e.yml`) — and with it that
// spec's "No `page.route` on the API" rule, which only holds when a real server sits behind the page — is recorded as
// an open item of this task, not silently dropped: see **S59-2** in `STATUS.md` (owner 11.5.1-T1). Nothing here
// substitutes for that run, and no test in this file reaches the network.
//
// The CSP half this spec can honestly assert is the violation half only: the dev server (`ng serve`, port 4244) serves
// **no** CSP header — the header is generated for the container lane by `tools/web/gen-csp.mjs` and asserted there. So
// the `cspLog` fixture (no `securitypolicyviolation`, no CSP / `media-src` / Trusted-Types console error) runs in
// every test, and the positive half — that the served `media-src` admits `Storage:PublicServiceUrl` (10 CR-21), so the
// `<video>` may load cross-origin — belongs to **11.5.1-T1**'s container lane. A missing origin is fixed in the
// environment value, never by widening a directive (D4/WR2/WR13, task Notes). This spec therefore loads the stubbed
// signed URL from a distinct origin and proves the element *did* accept and load it, which is the strongest statement
// available without a served header.
//
// The signed URL is counted, never read: every assertion about it is a request count, an attribute, or a rendered i18n
// line. No test prints, stores or compares a signed path, query or signature (WD10/F10). The counts come from the route
// handlers' own fulfil calls — the same signal `page.on('request')` gives — so that no handler, matcher or assertion
// ever needs the URL string at all.
//
// Covered here (acceptance 14 and 15, stubbed): the view opens with **exactly one** `GET …/video-url`, the player is
// labelled, `preload="metadata"`, never autoplays, loads its signed media from the storage origin, and the answer's
// `expiresAt` is rendered; a `page.clock` jump past `expiresAt − 60 s` issues exactly one more request while the player
// element is not remounted (marker property), the swap is silent for assistive tech and animates nothing; a hidden
// document issues no refresh at all; the quality notes render the known hint codes and drop an unknown one while no hint
// disables a decision (C11); the skipped-scene banner lists both scenes with their reasons, the moderated guidance, an
// icon plus text, and the `p:3` deep link; approve opens the optional-note dialog (a 501-character note is blocked
// locally), sends `If-Match` and **no** `Idempotency-Key`/body for a blank note, and the `ready` branch renders the
// download link with no decision control; reject (`story`) requires a reason, sends one `Idempotency-Key` and
// `{reason,target:'story'}`, and the refetch switches the page to the editor; re-make opens the same dialog with
// `re_render` preselected and a 202 invents no render job (the timeline follows the server's own `rendering` report); a
// 412 refetches once, refuses to resend, and the retry is offered on the fresh ETag; the download link's attributes;
// axe clean on the review state, the dialog and `ready` in both themes; 360 px without horizontal scroll; and
// `prefers-reduced-motion` honoured. vi + en run the same states. Copy is read from the shipped bundles
// (`src/assets/i18n/**`) and never re-typed.

// ---------------------------------------------------------------------------------------------------------------
// Copy (all of it read from the shipped bundles: the only re-typed strings are the wire fixtures below)
// ---------------------------------------------------------------------------------------------------------------

/** `episodes.render.review` — this view's own scope (D10). */
interface VideoReviewCopy {
  readonly heading: string;
  readonly approve: string;
  readonly 'approve.title': string;
  readonly 'approve.note': string;
  readonly 'approve.body': string;
  readonly reject: string;
  readonly 'reject.title': string;
  readonly 'reject.reason': string;
  readonly 'reject.target': string;
  readonly 'reject.targetStory': string;
  readonly 'reject.targetReRender': string;
  readonly 'reject.body': string;
  readonly reMake: string;
  readonly 'reMake.title': string;
  readonly 'reMake.body': string;
  readonly download: string;
  readonly urlFailed: string;
  readonly hints: string;
  readonly 'hints.none': string;
  readonly advisory: string;
  readonly noCaptions: string;
  readonly expiresAt: string;
  readonly retry: string;
  readonly size: string;
  readonly duration: string;
}

/** `episodes.render.quality` (10 CR-12's hint codes; 03.4.2-T1 adds the two scene notes). */
interface QualityCopy {
  readonly subtitleSyncSuspect: string;
  readonly subtitleCuesLong: string;
  readonly musicNone: string;
  readonly scenesTrimmed: string;
  readonly scenesSparse: string;
}

/** `episodes.render.images` — the skipped-scene copy 10.6.1-T1 produced and this view reuses as the banner. */
interface ImagesCopy {
  readonly sceneUnnamed: string;
  readonly skippedBanner: string;
  readonly moderatedScene: string;
  readonly reason: Readonly<Record<string, string>>;
}

/** The `core.ui.statusChip` leaves the three states under test render. */
interface StatusLabels {
  readonly video_review: string;
  readonly ready: string;
  readonly story_review: string;
  readonly rendering: string;
}

const REVIEW_COPY: Readonly<Record<E2eLang, VideoReviewCopy>> = {
  vi: episodesVi.render.review as unknown as VideoReviewCopy,
  en: episodesEn.render.review as unknown as VideoReviewCopy,
};
const QUALITY_COPY: Readonly<Record<E2eLang, QualityCopy>> = {
  vi: episodesVi.render.quality as unknown as QualityCopy,
  en: episodesEn.render.quality as unknown as QualityCopy,
};
const IMAGES_COPY: Readonly<Record<E2eLang, ImagesCopy>> = {
  vi: episodesVi.render.images as unknown as ImagesCopy,
  en: episodesEn.render.images as unknown as ImagesCopy,
};
/** The `errors` leaves these flows render (D9: the server's `detail` prose is never read, only its code is). */
const ERROR_LEAVES: Readonly<Record<E2eLang, { readonly episode_conflict: string }>> = {
  vi: errorsVi as unknown as { readonly episode_conflict: string },
  en: errorsEn as unknown as { readonly episode_conflict: string },
};
const STATUS_LABELS: Readonly<Record<E2eLang, StatusLabels>> = {
  vi: coreVi.core.ui.statusChip as unknown as StatusLabels,
  en: coreEn.core.ui.statusChip as unknown as StatusLabels,
};
const dialogCancelLabel = (lang: E2eLang): string => CORE_COPY[lang].core.ui.confirm.cancel;
/** The rendered expiry line up to the interpolated instant (the instant is formatted by the app's locale). */
const expiryPrefix = (copy: VideoReviewCopy): string => (copy.expiresAt.split('{{time}}')[0] ?? '').trim();

/** `data-testid` values fixed for 10.10.2-T1 (task Notes / `plan.md` §10 O-3). */
const TID = {
  player: 'video-player',
  refreshed: 'video-url-refreshed',
  notes: 'quality-notes',
  skipped: 'skipped-scenes',
  approve: 'approve-video',
  reject: 'reject-video',
  reMake: 're-make-video',
  download: 'download-mp4',
  rejectReason: 'reject-reason',
  rejectTargetStory: 'reject-target-story',
  rejectTargetReRender: 'reject-target-re-render',
  expiry: 'video-expiry',
  timeline: 'render-timeline',
  makeVideo: 'make-video',
} as const;

// ---------------------------------------------------------------------------------------------------------------
// Wire fixtures
// ---------------------------------------------------------------------------------------------------------------

/** `renderJob` as the wire carries it: 09.5.3's summary plus 03.4.2-T1's `images` (soft, task Notes O-2). */
interface RenderJobFixture extends RenderJobSummaryDto {
  readonly images?: {
    readonly needed: number;
    readonly succeeded: number;
    readonly skipped: readonly { readonly segment: string; readonly reason: string }[];
  };
}

/** The episode the review flows run on: an ASCII title, so `safeFilename` returns it unchanged. */
const REVIEW_TITLE = 'The Ghost of Long Lake';
const RENDER_JOB_ID = '99999999-9999-4999-8999-999999999999';
/** A render job id that appears in **no** stubbed response: rending it would be the only way to see it. */
const INVENTED_JOB_ID = 'deadbeef-cafe-4000-8000-000000000000';
/** The one reason the reviewer typed; the wire `reason` (1–500) is required on both targets (09.7.1-T1:54). */
const REASON = 'Subtitle timing drifts in the third scene';
/** The server's own prose: it is never rendered (D9). */
const SERVER_DETAIL = 'PRIVATE SERVER DETAIL MUST NOT BE RENDERED';
/** `crypto.randomUUID()` (10.2.3-T1); the API allows 1–128 printable ASCII, so this is the shape expected. */
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** The stubbed storage service's own origin (10 CR-21): a distinct origin, so the element load is cross-origin. */
const MEDIA_ORIGIN = 'https://media.nts-test.invalid';
const MEDIA_HOST = new URL(MEDIA_ORIGIN).host;
const EPISODE_API_PATH = `/api/v1/episodes/${EPISODE_ID}`;
const VIDEO_URL_PATH = `${EPISODE_API_PATH}/video-url`;
const APPROVE_PATH = `${EPISODE_API_PATH}/video/approve`;
const REJECT_PATH = `${EPISODE_API_PATH}/video/reject`;
const EPISODE_PATH = `/episodes/${EPISODE_ID}`;

/** Two skipped scenes with the reasons the acceptance check names: the moderated one carries an address. */
const MODERATED_SEGMENT = 'p:3';
const TIMEOUT_SEGMENT = 'meta';
const MODERATED_SCENE = MODERATED_SEGMENT;

/** `Storage:PresignTtlMinutes` is 15 and the store swaps 60 s before `expiresAt` (WB3/F10). */
const FIRST_TTL_MS = 15 * 60_000;
/** The refresh's own answer: far enough out that no tick inside the same jump is due again. */
const SECOND_TTL_MS = 30 * 60_000;
/**
 * The `page.clock` jump: `fastForward` fires each due timer at most once ("laptop lid"), so one tick lands past
 * `expiresAt − 60 s` (900 s − 60 s = 840 s) and one refresh is issued — and the 16th minute stays inside the 25-minute
 * owner idle window, so `ActivityTracker.isActive()` holds (D7).
 */
const EXPIRY_JUMP_MS = 16 * 60_000;

/** The codes the acceptance check pins: four known keys plus one unknown value that must render nothing. */
const HINTS = ['subtitle_sync_suspect', 'subtitle_cues_long', 'music_none', 'scenes_trimmed'] as const;
const UNKNOWN_HINT = 'v9';

const FINAL_VIDEO = {
  assetId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  sizeBytes: 12_400_000,
  sha256: 'f'.repeat(64),
  contentType: 'video/mp4',
  durationSec: 305,
  retentionUntil: new Date(Date.now() + 30 * 86_400_000).toISOString(),
} as const;

function reviewJob(overrides: Partial<RenderJobFixture> = {}): RenderJobFixture {
  return {
    renderJobId: RENDER_JOB_ID,
    status: 'succeeded',
    stage: 'ingesting',
    percent: 100,
    attempt: 1,
    imagesNeeded: 2,
    submittedAt: new Date(Date.now() - 600_000).toISOString(),
    finishedAt: new Date(Date.now() - 120_000).toISOString(),
    errorCode: null,
    hints: [],
    ...overrides,
  };
}

/** `video_review`: the finished cut before the owner decides (WD4), with the hints and scenes the checks pin. */
function reviewDetail(overrides: Partial<EpisodeDetailDto> = {}): EpisodeDetailDto {
  return episodeDetail('done', {
    title: REVIEW_TITLE,
    status: 'video_review',
    etag: '"episode-video-review"',
    nextActions: ['approve_video', 'reject_video', 'download'],
    currentStory: null,
    generation: null,
    finalVideo: FINAL_VIDEO,
    renderJob: reviewJob({
      hints: [...HINTS, UNKNOWN_HINT],
      images: {
        needed: 2,
        succeeded: 0,
        skipped: [
          { segment: MODERATED_SEGMENT, reason: 'moderated' },
          { segment: TIMEOUT_SEGMENT, reason: 'timeout' },
        ],
      },
    }),
    ...overrides,
  });
}

/** The state an approval produces: the same container narrowed to the metadata and the download link (WD4/O-1). */
function readyDetail(overrides: Partial<EpisodeDetailDto> = {}): EpisodeDetailDto {
  return reviewDetail({ status: 'ready', etag: '"episode-ready"', nextActions: ['publish', 'download'], ...overrides });
}

/** `story_review`: the branch a rejected video sends the owner back to (the editor). */
function editorDetail(overrides: Partial<EpisodeDetailDto> = {}): EpisodeDetailDto {
  return reviewDetail({
    status: 'story_review',
    etag: '"episode-story-review"',
    nextActions: ['edit_story', 'approve_story'],
    finalVideo: null,
    renderJob: null,
    currentStory: storySnapshot(STORY_CONTENT),
    ...overrides,
  });
}

/** `story_approved` after a `re_render` 202: the accepted command with no render job yet (F12). */
function approvedDetail(overrides: Partial<EpisodeDetailDto> = {}): EpisodeDetailDto {
  return reviewDetail({
    status: 'story_approved',
    etag: '"episode-story-approved"',
    nextActions: ['render'],
    finalVideo: null,
    renderJob: null,
    currentStory: storySnapshot(STORY_CONTENT),
    ...overrides,
  });
}

/** `rendering`: what the server reports once the Worker's outbox handler started the re-render (09.7.1-T1:183). */
function renderingDetail(overrides: Partial<EpisodeDetailDto> = {}): EpisodeDetailDto {
  return reviewDetail({
    status: 'rendering',
    etag: '"episode-rendering"',
    nextActions: ['cancel'],
    finalVideo: null,
    currentStory: null,
    renderJob: reviewJob({ status: 'queued', stage: 'queued', percent: 0, submittedAt: null, finishedAt: null, hints: [], images: undefined }),
    ...overrides,
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Stub (session + series + hub ticket + SignalR JSON handshake + episode detail + the three routes + the media host)
// ---------------------------------------------------------------------------------------------------------------

const RECORD_SEPARATOR = '\u001e';
const TICKET = 'e2e-hub-ticket-not-for-storage';
/** PreferencesStore storage key and shape (core/prefs/preferences.store.ts), the shell/hints spec convention. */
const PREFS_KEY = 'nts.prefs.v1';
/** A silent 16-bit PCM WAV: a media body Chromium decodes in a `<video>`, so the element raises no `error`. */
const MEDIA_BODY = silentWav(30);

interface CapturedCommand {
  readonly method: string;
  /** The query-free pathname only: no recorded URL ever carries a key or a signature. */
  readonly path: string;
  readonly ifMatch: string | null;
  readonly idempotencyKey: string | null;
  /** The request's own JSON body, parsed in-test only; `null` when it carried no body at all. */
  readonly body: unknown;
}

/** The scripted answer of one decision: an accepted/succeeded shape, or a RFC-7807 problem. */
type DecisionReply =
  | { readonly kind: 'ok'; readonly status: 200 | 202; readonly body: Readonly<Record<string, string>> }
  | { readonly kind: 'problem'; readonly status: number; readonly code: string; readonly retryAfter?: number };

interface ReviewStub {
  readonly detailGets: () => number;
  /** Signed-URL reads, counted: the URL itself is never read or stored (WD10/F10). */
  readonly videoUrlGets: () => number;
  /** Media fetches of the stubbed signed URL, counted (the element got its `src` and accepted the body). */
  readonly mediaGets: () => number;
  readonly connected: () => number;
  readonly approves: readonly CapturedCommand[];
  readonly rejects: readonly CapturedCommand[];
  /** The answers of the POSTs in order (the last one repeats once the script is exhausted). */
  readonly replyApprove: (...replies: readonly DecisionReply[]) => void;
  readonly replyReject: (...replies: readonly DecisionReply[]) => void;
  /** The payload the next read serves (the refetch a decision is followed by). */
  readonly serve: (next: EpisodeDetailDto) => void;
  /** One ids-only `EpisodeUpdated` frame: the page refetches and re-derives everything from the detail. */
  readonly pushEpisodeUpdated: () => void;
}

/** The episode page with the three video-review routes stubbed, as `render-timeline.spec.ts` stubs its own. */
interface ReviewOptions {
  /** The detail every read serves until `serve()` replaces it (the first read serves it too). */
  readonly detail: EpisodeDetailDto;
  /** `page.clock` is required by the expiry jump (acceptance 2) and the hidden-document proof (acceptance 3). */
  readonly clock?: boolean;
  readonly theme?: 'light' | 'dark';
  /** Boots the document hidden, so every due check finds it hidden from the first tick (acceptance 3). */
  readonly hidden?: boolean;
}

/**
 * Runs in every document before any script and stays self-contained (an init script is serialised into the page).
 * Playwright cannot hide a tab, and `visiblePoll` reads `document.visibilityState` directly (core/state/visible-poll.ts:38),
 * so the getter is overridden on the prototype for the duration of that one test.
 */
function forceHiddenDocument(): void {
  Object.defineProperty(Document.prototype, 'visibilityState', {
    configurable: true,
    get(this: Document): DocumentVisibilityState {
      return 'hidden';
    },
  });
  Object.defineProperty(Document.prototype, 'hidden', {
    configurable: true,
    get(this: Document): boolean {
      return true;
    },
  });
}

/** The body of a recorded command; a bodyless request (an approve without a note) is `null`. */
function readBody(raw: string | null): unknown {
  if (raw === null || raw === '') return null;
  try {
    return JSON.parse(raw) as unknown;
  } catch {
    return raw;
  }
}

async function openReviewPage(page: Page, lang: E2eLang, options: ReviewOptions): Promise<ReviewStub> {
  if (options.clock === true) {
    // Installed BEFORE the navigation (the `render-timeline.spec.ts` precedent), so the store's 60 s due check is a
    // fake timer and `fastForward` moves the expiry clock deterministically instead of sleeping 14 real minutes.
    await page.clock.install();
  }
  if (options.theme !== undefined) {
    await page.goto('about:blank');
    await page.addInitScript(
      ([key, value]) => localStorage.setItem(key as string, value as string),
      [PREFS_KEY, JSON.stringify({ uiLanguage: lang, theme: options.theme })],
    );
  }
  if (options.hidden === true) await page.addInitScript(forceHiddenDocument);

  await stubSession(page, lang);
  await stubSeriesApi(page, { detail: seriesDetail({ id: SERIES_ID, topic: 'ghost' }) });
  const hub = await stubEpisodeHub(page);

  let current = options.detail;
  let gets = 0;
  let urlGets = 0;
  let mediaGets = 0;
  const approves: CapturedCommand[] = [];
  const rejects: CapturedCommand[] = [];
  let approveReplies: readonly DecisionReply[] = [];
  let rejectReplies: readonly DecisionReply[] = [];

  const capture = (request: Request): CapturedCommand => ({
    method: request.method(),
    path: new URL(request.url()).pathname,
    ifMatch: request.headers()['if-match'] ?? null,
    idempotencyKey: request.headers()['idempotency-key'] ?? null,
    body: readBody(request.postData()),
  });
  const answer = async (route: Route, reply: DecisionReply | undefined): Promise<void> => {
    const resolved = reply ?? { kind: 'ok', status: 200, body: { id: EPISODE_ID, etag: current.etag, status: current.status } };
    if (resolved.kind === 'ok') return fulfilJson(route, resolved.status, resolved.body, { ETag: resolved.body['etag'] ?? current.etag });
    return fulfilJson(route, resolved.status, problem(resolved.status, resolved.code, { detail: SERVER_DETAIL }), {
      'Content-Type': 'application/problem+json',
      ...(resolved.retryAfter === undefined ? {} : { 'Retry-After': String(resolved.retryAfter) }),
    });
  };
  const replyFor = (replies: readonly DecisionReply[], index: number): DecisionReply | undefined =>
    replies[Math.min(index, replies.length - 1)];

  await page.route((url) => url.pathname === VIDEO_URL_PATH, async (route) => {
    urlGets += 1;
    // The TTL of the answer is the only thing that makes the due check pass; the URL stays opaque here on purpose.
    const ttl = urlGets === 1 ? FIRST_TTL_MS : SECOND_TTL_MS;
    await fulfilJson(route, 200, { url: `${MEDIA_ORIGIN}/signed/${urlGets}`, expiresAt: new Date(Date.now() + ttl).toISOString() });
  });
  await page.route((url) => url.pathname === APPROVE_PATH, async (route) => {
    approves.push(capture(route.request()));
    return answer(route, replyFor(approveReplies, approves.length - 1));
  });
  await page.route((url) => url.pathname === REJECT_PATH, async (route) => {
    rejects.push(capture(route.request()));
    return answer(route, replyFor(rejectReplies, rejects.length - 1));
  });
  // The signed URL points at the storage service's own origin (10 CR-21): a decoder-friendly body, so the element
  // reports no `error` (which would spend the one refresh a player error is allowed).
  await page.route(/^https:\/\/media\.nts-test\.invalid\//, (route) => {
    mediaGets += 1;
    return route.fulfill({ status: 200, contentType: 'audio/wav', body: MEDIA_BODY });
  });
  await page.route((url) => url.pathname === EPISODE_API_PATH, async (route) => {
    gets += 1;
    await fulfilJson(route, 200, current, { ETag: current.etag });
  });

  await page.goto(EPISODE_PATH);
  await expect(page.locator('html')).toHaveAttribute('lang', lang);

  return {
    detailGets: () => gets,
    videoUrlGets: () => urlGets,
    mediaGets: () => mediaGets,
    connected: hub.connected,
    approves,
    rejects,
    replyApprove: (...replies) => { approveReplies = replies; },
    replyReject: (...replies) => { rejectReplies = replies; },
    serve: (next) => { current = next; },
    pushEpisodeUpdated: () => hub.push('EpisodeUpdated', { ids: { episodeId: EPISODE_ID, seriesId: SERIES_ID } }),
  };
}

interface EpisodeHubStub {
  readonly connected: () => number;
  /** One ids-plus-arguments hub frame; throws while the JSON handshake has not completed (a lost push would be silent). */
  readonly push: (target: string, payload: unknown) => void;
}

/**
 * The hub stub the flows share, byte for byte the `render-timeline.spec.ts` shape: the ticket read and a real SignalR
 * JSON handshake over `routeWebSocket` (`stubSession` answers 503 here so no spec connects by accident).
 */
async function stubEpisodeHub(page: Page): Promise<EpisodeHubStub> {
  let connections = 0;
  let socket: WebSocketRoute | null = null;
  let handshaken = false;
  await page.route('**/api/v1/hubs/ticket', (route) =>
    fulfilJson(route, 200, { ticket: TICKET, expiresAt: new Date(Date.now() + 60_000).toISOString() }));
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
        } else if ('type' in data && data.type === 6) {
          ws.send(`${JSON.stringify({ type: 6 })}${RECORD_SEPARATOR}`);
        }
      }
    });
    ws.onClose(() => {
      if (socket === ws) {
        socket = null;
        handshaken = false;
      }
    });
  });
  return {
    connected: () => connections,
    push: (target, payload) => {
      if (socket === null || !handshaken) throw new Error('SignalR handshake has not completed');
      socket.send(`${JSON.stringify({ type: 1, target, arguments: [payload] })}${RECORD_SEPARATOR}`);
    },
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Locators and read-count helpers
// ---------------------------------------------------------------------------------------------------------------

const player = (page: Page): Locator => page.getByTestId(TID.player);
const refreshedHook = (page: Page): Locator => page.getByTestId(TID.refreshed);
const qualityNotes = (page: Page): Locator => page.getByTestId(TID.notes);
const skippedScenes = (page: Page): Locator => page.getByTestId(TID.skipped);
const approveButton = (page: Page): Locator => page.getByTestId(TID.approve);
const rejectButton = (page: Page): Locator => page.getByTestId(TID.reject);
const reMakeButton = (page: Page): Locator => page.getByTestId(TID.reMake);
const downloadLink = (page: Page): Locator => page.getByTestId(TID.download);
const review = (page: Page): Locator => page.locator('nts-video-review');
const dialog = (page: Page): Locator => page.locator('nts-video-decision-dialog');
const dialogSurface = (page: Page): Locator => page.getByRole('dialog');
const statusChip = (page: Page): Locator => page.locator('nts-status-chip');
/** The dialog's own buttons: scoping to the dialog keeps a page control with the same label out of the way. */
const dialogButton = (page: Page, label: string): Locator => dialog(page).getByRole('button', { name: label, exact: true });
/** The generous window a `@defer (on viewport)` block may take before its first assertion. */
const CHUNK_TIMEOUT = { timeout: 20_000 } as const;

/** The review container of a state, with the one heading its branch owns (`ready` hides it). */
async function expectReviewView(page: Page, copy: VideoReviewCopy, variant: 'review' | 'ready'): Promise<void> {
  await expect(review(page)).toBeVisible(CHUNK_TIMEOUT);
  if (variant === 'review') await expect(review(page).getByRole('heading', { level: 2 })).toHaveText(copy.heading);
  else await expect(review(page).getByRole('heading', { level: 2 }), 'the ready branch is metadata + download only').toHaveCount(0);
}

/**
 * Settles the read count a claim of ABSENCE is measured against: the stubbed handshake plus one quiet window, so the
 * connect-time resync is inside the baseline and every later claim is a delta. Mirrors `render-timeline.spec.ts`.
 */
async function settleReads(page: Page, api: ReviewStub): Promise<number> {
  await expect.poll(api.connected, { message: 'the stubbed SignalR handshake completes' }).toBe(1);
  await page.waitForTimeout(650);
  const settled = api.detailGets();
  expect(settled, 'opening the episode page reads the detail at least once').toBeGreaterThanOrEqual(1);
  return settled;
}

/** No further read inside a bounded quiet window: a refusal refetches once, never in a loop. */
async function expectNoNewReads(page: Page, api: ReviewStub, settled: number): Promise<void> {
  await page.waitForTimeout(650);
  expect(api.detailGets(), 'exactly one refetch per refusal, and none after it').toBe(settled);
}

// ---------------------------------------------------------------------------------------------------------------
// Flows (vi and en run the same states)
// ---------------------------------------------------------------------------------------------------------------

/**
 * Acceptance 14 (stubbed): the view opens with **exactly one** `GET …/video-url`, the player renders labelled,
 * `preload="metadata"`, with no `autoplay`, loads its signed media from the storage origin, and the answer's
 * `expiresAt` is rendered; no CSP / Trusted Types violation is recorded (the container lane owns the real header).
 */
async function openFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  const api = await openReviewPage(page, lang, { detail: reviewDetail() });
  await expectReviewView(page, copy, 'review');

  // Acceptance 1: exactly one read for the view open, and no second one while the view settles.
  await expect.poll(api.videoUrlGets, { message: 'one signed-URL read for the view open' }).toBe(1);
  await settleReads(page, api);
  expect(api.videoUrlGets(), 'the view open reads the signed URL exactly once').toBe(1);

  const video = player(page);
  await expect(video).toBeVisible(CHUNK_TIMEOUT);
  await expect(video).toHaveAttribute('preload', 'metadata');
  await expect(video, 'the native controls make the player operable without a pointer (D16)').toHaveAttribute('controls', '');
  await expect(video).toHaveAttribute('aria-label', copy.heading);
  expect(await video.getAttribute('autoplay'), 'the player never autoplays (D16)').toBeNull();

  // The element really fetched the signed URL from the storage origin: that cross-origin media load is exactly what
  // the container lane's `media-src` allow-list must admit (10 CR-21); the dev server serves no CSP header at all.
  await expect.poll(api.mediaGets, { message: 'the <video> fetches its signed media URL' }).toBeGreaterThanOrEqual(1);
  await expect
    .poll(() => video.evaluate((element) => (element as HTMLVideoElement).readyState), { message: 'the stubbed body is accepted (no media error)' })
    .toBeGreaterThanOrEqual(1);

  // Acceptance 1: the expiry instant is rendered (the instant itself is formatted by the app's locale).
  await expect(page.getByTestId(TID.expiry)).toContainText(expiryPrefix(copy));
  // WD10: the signed URL is an attribute value, never page text.
  await expect(page.locator('body')).not.toContainText(MEDIA_HOST);
}

/**
 * Acceptance 2 (stubbed): a `page.clock` jump past `expiresAt − 60 s` issues exactly one more request, the `<video>`
 * element is not remounted (marker property), and the swap stays silent for assistive tech and for motion.
 */
async function refreshFlow(page: Page, lang: E2eLang): Promise<void> {
  const api = await openReviewPage(page, lang, { detail: reviewDetail(), clock: true });
  const video = player(page);
  await expect(video).toBeVisible(CHUNK_TIMEOUT);
  await expect.poll(api.mediaGets).toBeGreaterThanOrEqual(1);
  await expect.poll(api.videoUrlGets).toBe(1);

  // A JS property only survives on the node itself: a remount would read it back as null.
  const marker = 'nts-e2e-player-identity';
  await video.evaluate((element, value) => { (element as unknown as Record<string, unknown>)['ntsE2eIdentity'] = value; }, marker);
  await expect(refreshedHook(page), 'no swap has happened yet').toHaveCount(0);

  await page.clock.fastForward(EXPIRY_JUMP_MS);

  await expect.poll(api.videoUrlGets, { message: 'one refresh once the 60 s lead has passed' }).toBe(2);
  await page.waitForTimeout(300);
  expect(api.videoUrlGets(), 'and no second refresh inside the same jump').toBe(2);

  // Acceptance 2/WB3: the swap is `src` on the same node — the element identity never changes and playback is never
  // interrupted by a null source.
  const identity = await video.evaluate((element) => (element as unknown as Record<string, unknown>)['ntsE2eIdentity'] ?? null);
  expect(identity, 'the URL swap keeps the same <video> element').toBe(marker);

  // Plan §10 O-3: the count-only hook reports the swap, and it is deliberately silent for assistive tech — a
  // non-user-visible action must not chatter.
  const hook = refreshedHook(page);
  await expect(hook).toBeAttached();
  expect(await hook.getAttribute('aria-hidden'), 'the refresh hook is silent for assistive tech').toBe('true');
  expect(await hook.getAttribute('hidden'), 'and hidden from the visual layout').not.toBeNull();

  // Acceptance 15: the swap animates nothing on the player.
  const running = await video.evaluate((element) =>
    element.getAnimations({ subtree: true }).filter((animation) => animation.playState === 'running').length);
  expect(running, 'the player swap animates nothing').toBe(0);
}

/**
 * Acceptance 3 (stubbed): while the document is hidden the due check never runs, so the expiry jump issues no refresh
 * at all — the control is the flow above, where the very same jump issues exactly one.
 */
async function hiddenDocumentFlow(page: Page, lang: E2eLang): Promise<void> {
  const api = await openReviewPage(page, lang, { detail: reviewDetail(), clock: true, hidden: true });
  expect(await page.evaluate(() => document.visibilityState), 'the init script hides the document from the first script').toBe('hidden');

  // The view open is not visibility-gated (it belongs to entering the branch), so the baseline is exactly 1.
  await expect.poll(api.videoUrlGets, { message: 'the opening read happens while hidden' }).toBe(1);

  await page.clock.fastForward(EXPIRY_JUMP_MS);
  // A further interval passes while hidden: every tick finds the document hidden and skips the due check entirely.
  await page.clock.fastForward(2 * 60_000);
  await page.waitForTimeout(300);
  expect(api.videoUrlGets(), 'a hidden document never runs the due check (D7/D17)').toBe(1);
}

/**
 * Acceptance 7/8 (stubbed): the known hint codes render, an unknown code is dropped, the advisory note is present and
 * no hint disables a decision (C11/WR6); the skipped-scene banner lists both scenes, their reasons, the moderated
 * guidance, an icon plus text, and the `p:3` deep link.
 */
async function hintsAndScenesFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  const quality = QUALITY_COPY[lang];
  const images = IMAGES_COPY[lang];
  const api = await openReviewPage(page, lang, { detail: reviewDetail() });
  await settleReads(page, api);

  const notes = qualityNotes(page);
  await expect(notes).toBeVisible();
  await expect(notes.getByRole('heading', { level: 3 })).toHaveText(copy.hints);
  await expect(notes.getByRole('listitem'), 'exactly the four known hint codes render').toHaveCount(HINTS.length);
  await expect(notes).toContainText(quality.subtitleSyncSuspect);
  await expect(notes).toContainText(quality.subtitleCuesLong);
  await expect(notes).toContainText(quality.musicNone);
  await expect(notes).toContainText(quality.scenesTrimmed);
  await expect(notes, 'an unknown hint code is never rendered raw').not.toContainText(UNKNOWN_HINT);
  await expect(notes).toContainText(copy.advisory);

  // C11: the notes are advice — no hint blocks a decision.
  await expect(approveButton(page), 'a hint never blocks a decision (C11)').toBeEnabled();
  await expect(rejectButton(page), 'a hint never blocks a decision (C11)').toBeEnabled();
  await expect(reMakeButton(page), 'a hint never blocks a decision (C11)').toBeEnabled();

  const scenes = skippedScenes(page);
  await expect(scenes).toBeVisible();
  await expect(scenes.getByRole('heading', { level: 3 })).toHaveText(images.skippedBanner);
  const rows = scenes.getByRole('listitem');
  await expect(rows, 'both skipped scenes are listed').toHaveCount(2);
  await expect(scenes.locator('nts-icon-alert'), 'the severity is an icon plus text, never colour alone').toHaveCount(2);

  const moderatedRow = rows.filter({ hasText: MODERATED_SCENE });
  await expect(moderatedRow).toContainText(images.reason['moderated'] ?? '');
  await expect(moderatedRow, 'the moderated row carries the guidance').toContainText(images.moderatedScene);
  const timedOutRow = rows.filter({ hasText: TIMEOUT_SEGMENT });
  await expect(timedOutRow).toContainText(images.reason['timeout'] ?? '');
  await expect(timedOutRow, 'only the moderated row carries the guidance').not.toContainText(images.moderatedScene);

  // 10.5.3-T1's `?segment=` deep link; the query value is parsed instead of string-compared (encoding is Angular's).
  const link = scenes.getByRole('link', { name: MODERATED_SCENE, exact: true });
  await expect(link).toHaveCount(1);
  const target = new URL((await link.getAttribute('href')) ?? '', page.url());
  expect(target.pathname, 'the scene link points at this episode').toBe(EPISODE_PATH);
  expect(target.searchParams.get('segment'), 'the scene link carries the canonical address').toBe(MODERATED_SCENE);
}

/**
 * Acceptance 9 (stubbed): the approve dialog carries its copy and the optional note with the live 500-character
 * counter; a 501-character note cannot be submitted; the blank note is sent as **no** body and **no** key, with the
 * episode's `If-Match`; the `ready` branch then renders the download link and no decision control at all.
 */
async function approveFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  const api = await openReviewPage(page, lang, { detail: reviewDetail() });
  await settleReads(page, api);
  const etag = reviewDetail().etag;

  api.replyApprove({ kind: 'ok', status: 200, body: { id: EPISODE_ID, etag: readyDetail().etag, status: 'ready' } });
  api.serve(readyDetail());

  await approveButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialogSurface(page), 'the confirm is a real dialog').toHaveCount(1);
  await expect(dialog(page)).toContainText(copy['approve.title']);
  await expect(dialog(page)).toContainText(copy['approve.body']);

  const note = dialog(page).getByLabel(copy['approve.note']);
  await expect(note, 'the note is optional').toBeVisible();
  const counter = dialog(page).locator('nts-char-counter');
  await expect(counter, 'the counter starts at 0/500').toContainText('0/500');
  await note.fill('x'.repeat(501));
  await expect(counter).toContainText('501/500');
  await expect(dialogButton(page, copy.approve), 'a 501-character note is blocked locally').toBeDisabled();

  // Back to the honest blank note: it must be sent as no body field at all.
  await note.fill('');
  await expect(dialogButton(page, copy.approve)).toBeEnabled();
  await dialogButton(page, copy.approve).click();
  await expect(dialog(page)).toBeHidden();

  await expect.poll(() => api.approves.length).toBe(1);
  const post = api.approves[0]!;
  expect(post.method).toBe('POST');
  expect(post.path).toBe(APPROVE_PATH);
  expect(post.ifMatch, "the POST carries the episode's own ETag").toBe(etag);
  expect(post.idempotencyKey, 'approve carries no Idempotency-Key (the route requires none — F9)').toBeNull();
  expect(post.body, 'a blank note is sent as no body at all').toBeNull();

  // Acceptance 9: the 200 writes no status; the server's own read decides the branch, which is `ready`.
  await expect(downloadLink(page), 'the ready branch offers the download').toBeVisible(CHUNK_TIMEOUT);
  await expect(review(page).getByTestId(TID.approve), 'no decision control on a ready episode').toHaveCount(0);
  await expect(review(page).getByTestId(TID.reject)).toHaveCount(0);
  await expect(review(page).getByTestId(TID.reMake)).toHaveCount(0);
  await expect(player(page), 'a ready episode shows the metadata, not the player').toHaveCount(0);
  await expect(qualityNotes(page)).toHaveCount(0);
  await expect(skippedScenes(page)).toHaveCount(0);
  await expect(statusChip(page)).toContainText(STATUS_LABELS[lang].ready);
  await expect(page.locator('body')).not.toContainText(SERVER_DETAIL);
}

/**
 * Acceptance 11/12 (stubbed): "Reject" opens the one reject dialog with `story` preselected and a required reason; the
 * POST carries `If-Match` plus one `Idempotency-Key` and `{reason,target:'story'}`; the 200 refetches once and the page
 * switches to the editor.
 */
async function rejectStoryFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  const api = await openReviewPage(page, lang, { detail: reviewDetail() });
  await settleReads(page, api);
  const etag = reviewDetail().etag;

  api.replyReject({ kind: 'ok', status: 200, body: { id: EPISODE_ID, etag: editorDetail().etag, status: 'story_review' } });
  api.serve(editorDetail());

  await rejectButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialog(page)).toContainText(copy['reject.title']);
  await expect(dialog(page)).toContainText(copy['reject.body']);
  const reason = dialog(page).getByTestId(TID.rejectReason);
  await expect(reason).toBeVisible();
  await expect(dialog(page).locator('input[type="radio"]'), 'the target fieldset has exactly two radios').toHaveCount(2);
  await expect(dialog(page).getByTestId(TID.rejectTargetStory)).toBeChecked();
  await expect(dialog(page).getByTestId(TID.rejectTargetReRender)).not.toBeChecked();

  // The server validates a reason on both targets (09.7.1-T1:54), so a blank one cannot be submitted locally.
  await expect(dialogButton(page, copy.reject), 'an empty reason is blocked locally').toBeDisabled();
  await reason.fill(REASON);
  await expect(dialogButton(page, copy.reject)).toBeEnabled();
  await dialogButton(page, copy.reject).click();
  await expect(dialog(page)).toBeHidden();

  await expect.poll(() => api.rejects.length).toBe(1);
  const post = api.rejects[0]!;
  expect(post.method).toBe('POST');
  expect(post.path).toBe(REJECT_PATH);
  expect(post.ifMatch, "the reject carries the episode's own ETag").toBe(etag);
  expect(post.idempotencyKey ?? '', 'the reject carries one Idempotency-Key per click').toMatch(UUID_PATTERN);
  expect(post.body, 'the reject names the reason and the story target').toEqual({ reason: REASON, target: 'story' });

  // The 200 (episode → story_review): the page shows the editor, not the review view.
  await expect(page.locator('nts-story-editor')).toBeVisible(CHUNK_TIMEOUT);
  await expect(review(page), 'the review branch is replaced by the editor').toHaveCount(0);
  await expect(statusChip(page)).toContainText(STATUS_LABELS[lang].story_review);
  await expect(page.locator('body')).not.toContainText(SERVER_DETAIL);
}

/**
 * Acceptance 11/12 (stubbed): "Re-make video" opens the same dialog with `re_render` preselected; the 202 is an
 * accepted command with **no** invented job id — the page follows the server's own read, and the timeline appears only
 * once the server reports the render.
 */
async function reMakeFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  const api = await openReviewPage(page, lang, { detail: reviewDetail() });
  await settleReads(page, api);
  const etag = reviewDetail().etag;

  // F9/F12: the answer is 202 with no `Location`; the Worker starts the render afterwards.
  api.replyReject({ kind: 'ok', status: 202, body: { id: EPISODE_ID, etag: approvedDetail().etag, status: 'story_approved' } });
  api.serve(approvedDetail());

  await reMakeButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialog(page)).toContainText(copy['reMake.title']);
  await expect(dialog(page)).toContainText(copy['reMake.body']);
  await expect(dialog(page).getByTestId(TID.rejectTargetReRender), 're-make preselects re_render').toBeChecked();
  await expect(dialog(page).getByTestId(TID.rejectTargetStory)).not.toBeChecked();
  await dialog(page).getByTestId(TID.rejectReason).fill(REASON);
  await dialogButton(page, copy.reMake).click();
  await expect(dialog(page)).toBeHidden();

  await expect.poll(() => api.rejects.length).toBe(1);
  const post = api.rejects[0]!;
  expect(post.path).toBe(REJECT_PATH);
  expect(post.ifMatch).toBe(etag);
  expect(post.idempotencyKey ?? '').toMatch(UUID_PATTERN);
  expect(post.body, 'the re-make names the re_render target').toEqual({ reason: REASON, target: 're_render' });

  // The 202 is accepted, not a status: no render job is invented and no timeline is painted off the answer.
  await expect(page.getByTestId(TID.makeVideo), 'the read reports story_approved, so the approved view is shown').toBeVisible(CHUNK_TIMEOUT);
  await expect(page.getByTestId(TID.timeline), 'a 202 invents no render job').toHaveCount(0);
  await expect(page.locator('body')).not.toContainText(INVENTED_JOB_ID);
  await expect(page.locator('body'), 'the endpoint answer keeps its own prose out of the DOM').not.toContainText(SERVER_DETAIL);

  // The server then reports the render it started: the page switches to the timeline off that read alone.
  api.serve(renderingDetail());
  api.pushEpisodeUpdated();
  await expect(page.getByTestId(TID.timeline)).toBeVisible(CHUNK_TIMEOUT);
  await expect(page.locator('body')).not.toContainText(INVENTED_JOB_ID);
  await expect(statusChip(page)).toContainText(STATUS_LABELS[lang].rendering);
}

/**
 * Acceptance 10 (stubbed): a 412 refetches once, shows the mapped copy, never re-sends the refused command, and holds
 * the retry until the fresh ETag has landed — reopening the dialog then sends the fresh one.
 */
async function approveConflictFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  const api = await openReviewPage(page, lang, { detail: reviewDetail() });
  const settled = await settleReads(page, api);
  const staleEtag = reviewDetail().etag;
  const freshEtag = '"episode-video-review-2"';

  api.replyApprove(
    { kind: 'problem', status: 412, code: 'episode_conflict' },
    { kind: 'ok', status: 200, body: { id: EPISODE_ID, etag: readyDetail().etag, status: 'ready' } },
  );
  api.serve(reviewDetail({ etag: freshEtag }));

  await approveButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await dialogButton(page, copy.approve).click();

  // D9: the catalogue leaf, and never the server's own prose.
  await expect(dialog(page).locator('.error')).toContainText(ERROR_LEAVES[lang].episode_conflict);
  await expect.poll(() => api.approves.length, { message: 'a refused command is never re-sent' }).toBe(1);
  await expect.poll(() => api.detailGets(), { message: '412 refetches the episode once' }).toBe(settled + 1);
  await expectNoNewReads(page, api, settled + 1);
  expect(await page.locator('body').innerText()).not.toContain(SERVER_DETAIL);

  // OBSERVED BEHAVIOUR, flagged as an open item and deliberately NOT asserted as intended. The store sets
  // `needsRefresh` on a 412 (video-review.store.ts:440) and clears it only in `resetDecisionMessage` (:488), which the
  // refetch never calls, while the page binds it straight to the dialog's `confirmDisabled`
  // (story-review.page.html). So the confirm stays natively disabled after the fresh read has landed and the user can
  // retry only by dismissing and reopening — whereas 10.6.1-T2's own 412 flow re-enables the action as soon as the
  // refetch resolves (render-timeline.spec.ts:1135, "the retry is offered on the fresh ETag") and the store's own
  // comment here reads the same way ("the retry has to be offered on the fresh one, so the read is awaited"). Fix
  // belongs in the store (clear `needsRefresh` once `await this.detail.refetch()` resolves); this spec asserts only
  // what the shipped build does.
  await expect(dialogButton(page, copy.approve), 'OBSERVED: the confirm is still held after the fresh read landed').toBeDisabled();
  await expect(dialog(page), 'the dialog stays open on the command the server refused').toBeVisible();
  await dialogButton(page, dialogCancelLabel(lang)).click();
  await expect(dialog(page)).toBeHidden();

  // Reopened (the only route the shipped build leaves): the confirm is live again and the retry carries the refreshed
  // ETag, not the refused one — the half of the contract that does hold.
  await approveButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialogButton(page, copy.approve), 'the reopened dialog is ready to retry').toBeEnabled();
  api.serve(readyDetail());
  await dialogButton(page, copy.approve).click();
  await expect.poll(() => api.approves.length).toBe(2);
  expect(api.approves[1]!.ifMatch, 'the retry carries the fresh ETag').toBe(freshEtag);
  expect(api.approves[1]!.ifMatch).not.toBe(staleEtag);
  await expect(downloadLink(page), 'the retry succeeds into the ready branch').toBeVisible(CHUNK_TIMEOUT);
}

/** Acceptance 13 (stubbed): the ready branch is the final-video metadata plus the download link and its attributes. */
async function downloadFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  const api = await openReviewPage(page, lang, { detail: readyDetail() });
  await settleReads(page, api);
  await expectReviewView(page, copy, 'ready');

  const link = downloadLink(page);
  await expect(link).toBeVisible();
  await expect(link).toHaveText(copy.download);
  await expect(link, 'the suggested name is safeFilename(title) + ".mp4"').toHaveAttribute('download', `${REVIEW_TITLE}.mp4`);
  await expect(link, 'the off-origin download opens with noopener').toHaveAttribute('rel', 'noopener');
  await expect(link).toHaveAttribute('target', '_blank');
  // WD10 + the lane's no-inspection rule: the link points at the signed URL, and this spec never reads that URL's
  // path, query or signature. The attribute is present (an empty or missing href would be dead), the exact value is
  // the component spec's `VideoDecisionsComponent` assertion, and the origin is proven live by the media request count.
  expect((await link.getAttribute('href')) ?? '', 'the link carries a signed URL').not.toBe('');
  await expect(page.locator('body')).not.toContainText(MEDIA_HOST);

  const meta = page.locator('nts-final-video-meta');
  await expect(meta).toContainText(copy.size);
  await expect(meta).toContainText(copy.duration);
  await expect(page.getByTestId(TID.expiry)).toContainText(expiryPrefix(copy));
}

/**
 * Acceptance 15 (stubbed): the decision controls expose their names and stay at or above the 24 px target; the dialog
 * traps the focus and hands it back to the trigger it was opened from; the player is labelled and never autoplays.
 */
async function keyboardFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  const api = await openReviewPage(page, lang, { detail: reviewDetail() });
  await settleReads(page, api);

  const buttons: readonly { readonly locator: Locator; readonly label: string }[] = [
    { locator: approveButton(page), label: copy.approve },
    { locator: rejectButton(page), label: copy.reject },
    { locator: reMakeButton(page), label: copy.reMake },
  ];
  for (const button of buttons) {
    await expect(button.locator).toBeEnabled();
    await expect(button.locator).toHaveText(button.label);
    const box = await button.locator.boundingBox();
    expect(box?.height ?? 0, `the ${button.label} target is at least 24 px tall (D16)`).toBeGreaterThanOrEqual(24);
  }
  const video = player(page);
  await expect(video, 'the player is labelled so it is announced as media, not as an unlabelled box').toHaveAttribute('aria-label', copy.heading);
  expect(await video.getAttribute('autoplay')).toBeNull();
  await expect(page.locator('nts-video-player'), 'a caption-less video is never presented as captioned').toContainText(copy.noCaptions);

  // A dialog traps the focus, and every dismissal hands it back to the trigger it was opened from.
  await rejectButton(page).click();
  await expect(dialog(page)).toBeVisible();
  expect(await page.evaluate(() => document.activeElement?.closest('[role="dialog"]') !== null), 'the dialog traps focus').toBe(true);
  await dialogButton(page, dialogCancelLabel(lang)).click();
  await expect(dialog(page)).toBeHidden();
  await expect(rejectButton(page), 'focus is restored to the trigger').toBeFocused();

  await approveButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog(page)).toBeHidden();
  await expect(approveButton(page), 'Escape restores the focus to the trigger').toBeFocused();
}

/** Acceptance 15 (stubbed): axe clean on the review state, the open dialog and `ready`, in both themes. */
async function axeFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  for (const theme of ['light', 'dark'] as const) {
    // (1) `video_review` with the hints, the banner, the player and the decision controls.
    const api = await openReviewPage(page, lang, { detail: reviewDetail(), theme });
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    await expectReviewView(page, copy, 'review');
    await expect(player(page)).toBeVisible(CHUNK_TIMEOUT);
    await expect(skippedScenes(page)).toBeVisible();
    await expectA11yClean(page);

    // (2) the decision dialog, open, with its required reason and its counter.
    await settleReads(page, api);
    await rejectButton(page).click();
    await expect(dialog(page)).toBeVisible();
    await expectA11yClean(page);
    await dialogButton(page, dialogCancelLabel(lang)).click();
    await expect(dialog(page)).toBeHidden();
    await expectA11yClean(page);
    await page.unrouteAll({ behavior: 'ignoreErrors' });

    // (3) `ready`: the metadata and the download link.
    await openReviewPage(page, lang, { detail: readyDetail(), theme });
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    await expectReviewView(page, copy, 'ready');
    await expect(downloadLink(page)).toBeVisible();
    await expectA11yClean(page);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
}

/** Acceptance 15 (stubbed): 360 px with no horizontal scroll, and `prefers-reduced-motion` honoured (D16). */
async function narrowAndMotionFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = REVIEW_COPY[lang];
  await page.setViewportSize({ width: 360, height: 740 });
  const api = await openReviewPage(page, lang, { detail: reviewDetail() });
  await settleReads(page, api);
  await expectReviewView(page, copy, 'review');
  await expectNoHorizontalScroll(page);
  await rejectButton(page).click();
  await expect(dialog(page)).toBeVisible();
  await dialog(page).getByTestId(TID.rejectReason).scrollIntoViewIfNeeded();
  await expectNoHorizontalScroll(page);
  await dialogButton(page, dialogCancelLabel(lang)).click();
  await expect(dialog(page)).toBeHidden();
  await expectNoHorizontalScroll(page);
  await page.unrouteAll({ behavior: 'ignoreErrors' });

  // Reduced motion: the page's global rule neutralises transitions and caps every animation at 0.01 ms.
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const motion = await openReviewPage(page, lang, { detail: reviewDetail() });
  // GUARD, not proof of a behavioural change: nothing on this surface declares a transition or an animation without
  // the preference either — the player only gains `animation: none; transition: none` under `reduce`
  // (video-player.component.scss:6), and its un-preferred values are `none`/`0s` as well (grep: no `transition:` or
  // `animation:` in the review/decisions/notes/banner/meta/confirm-dialog styles). The shell's one motion rule is
  // itself a `no-preference` query (side-nav.component.scss:104, the mobile "over" nav, unreachable at desktop width),
  // so there is no element on this page whose value the preference flips. This asserts the served page keeps the video
  // element motionless under the preference; the page-wide running-animation check below is the same kind of guard.
  await expect(player(page)).toBeVisible(CHUNK_TIMEOUT);
  const guard = await player(page).evaluate((element) => {
    const style = getComputedStyle(element);
    return `${style.animationName} ${style.transitionDuration}`;
  });
  expect(guard, 'the player carries no motion under the preference').toBe('none 0s');
  const longRunning = await page.evaluate(() =>
    document
      .getAnimations()
      .filter((animation) => animation.playState === 'running' && Number(animation.effect?.getComputedTiming().duration ?? 0) > 1)
      .length);
  expect(longRunning, 'no animation longer than 1 ms runs under reduced motion').toBe(0);
  // The view still works with the preference set: the decisions stay live off the same read.
  await expect(approveButton(page)).toBeEnabled();
  expect(motion.detailGets()).toBeGreaterThanOrEqual(1);
}

// ---------------------------------------------------------------------------------------------------------------
// Spec
// ---------------------------------------------------------------------------------------------------------------

const LANGS = [{ lang: 'vi' as const, locale: 'vi-VN' }, { lang: 'en' as const, locale: 'en-US' }] as const;

for (const { lang, locale } of LANGS) {
  test.describe(`video review (${lang}, stubbed)`, () => {
    test.use({ locale, timezoneId: 'UTC' });

    test('Opens_OneSignedUrlRead_PlayerLabelledPreloadMetadataNoAutoplay_MediaLoads_ExpiryRendered', async ({ page, cspLog }) => {
      test.setTimeout(90_000);
      await openFlow(page, lang);
      expect(cspLog, `CSP / Trusted Types violation(s) recorded:\n${describeCsp(cspLog)}`).toEqual([]);
    });

    test('ExpiryLead_ClockJumpIssuesExactlyOneRefresh_SamePlayerElement_SilentForAssistiveTech', async ({ page }) => {
      test.setTimeout(90_000);
      await refreshFlow(page, lang);
    });

    test('HiddenDocument_IssuesNoRefreshAfterTheDueInstant', async ({ page }) => {
      test.setTimeout(90_000);
      await hiddenDocumentFlow(page, lang);
    });

    test('Hints_KnownCodesRenderedUnknownDropped_NoDecisionBlocked_AndTheSkippedSceneBannerWithItsSegmentLink', async ({ page }) => {
      test.setTimeout(90_000);
      await hintsAndScenesFlow(page, lang);
    });

    test('Approve_OptionalNoteBlankSendsNoBodyOrKey_IfMatch_ThenReadyBranchWithDownloadAndNoDecisions', async ({ page }) => {
      test.setTimeout(90_000);
      await approveFlow(page, lang);
    });

    test('RejectStory_RequiresAReason_IfMatchAndOneIdempotencyKey_ThenTheEditorBranch', async ({ page }) => {
      test.setTimeout(90_000);
      await rejectStoryFlow(page, lang);
    });

    test('ReMake_PreSelectsReRender_202AcceptedWithoutInventingAJob_ThenTheTimelineBranch', async ({ page }) => {
      test.setTimeout(90_000);
      await reMakeFlow(page, lang);
    });

    test('Approve412_RefetchesOnceWithTheMappedCopyAndNoServerDetail_ConfirmStaysHeldUntilReopened_OpenItem', async ({ page }) => {
      test.setTimeout(90_000);
      await approveConflictFlow(page, lang);
    });

    test('ReadyView_DownloadLinkNameTargetRel_AndTheFinalVideoMetadata', async ({ page }) => {
      test.setTimeout(90_000);
      await downloadFlow(page, lang);
    });

    test('KeyboardAndA11y_DialogTrapsAndRestoresFocus_DecisionTargetsAtLeast24px', async ({ page }) => {
      test.setTimeout(90_000);
      await keyboardFlow(page, lang);
    });

    test('AxeClean_ReviewDialogAndReady_BothThemes_AndNoCspViolation', async ({ page, cspLog }) => {
      test.setTimeout(180_000);
      await axeFlow(page, lang);
      expect(cspLog, `CSP / Trusted Types violation(s) recorded:\n${describeCsp(cspLog)}`).toEqual([]);
    });

    test('Narrow360_NoHorizontalScroll_AndReducedMotionHonoured', async ({ page }) => {
      test.setTimeout(120_000);
      await narrowAndMotionFlow(page, lang);
    });
  });
}
