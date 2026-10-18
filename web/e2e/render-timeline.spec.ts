import type { Locator, Page, WebSocketRoute } from '@playwright/test';
import type { EpisodeDetailDto } from '../src/app/shared/api/models/episode-detail-dto';
import type { RenderJobSummaryDto } from '../src/app/shared/api/models/render-job-summary-dto';
import coreEn from '../src/assets/i18n/core/en.json' with { type: 'json' };
import coreVi from '../src/assets/i18n/core/vi.json' with { type: 'json' };
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import episodesVi from '../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import errorsEn from '../src/assets/i18n/errors/en.json' with { type: 'json' };
import errorsVi from '../src/assets/i18n/errors/vi.json' with { type: 'json' };
import { expectA11yClean, waitForOverlayAnimationEnd } from './fixtures/axe';
import { describeCsp, expect, test } from './fixtures/csp.fixture';
import { episodeDetail, EPISODE_ID, SERIES_ID } from './fixtures/episodes-stubs';
import { expectNoHorizontalScroll, fulfilJson, problem, seriesDetail, stubSeriesApi, stubSession, type E2eLang } from './fixtures/series-stubs';
import { EDITOR_COPY, segmentGroup, storySnapshot, STORY_CONTENT, type StoryWire } from './fixtures/story-editor-stubs';

// 10.6.1-T1 — render timeline on the episode page: acceptance checks 9 and 10, STUBBED variant.
//
// Owner decision for this task: this lane runs no compose stack and no mock-mpt, so `GET /api/v1/episodes/{id}` and the
// realtime hub are stubbed exactly like `realtime.spec.ts` / `episodes-editor.spec.ts` do. The task file's real-stack
// wording (compose + mock-mpt with `MOCK_RENDER_SECONDS`, `MOCK_FAIL_MODE=quality:<check>`,
// `MOCK_IMAGE_REPORT=moderated_all` + `MOCK_FAIL_MODE=image_moderated`, run by 11.5.1-T1's `e2e.yml`) is recorded as an
// open item of this task, not silently dropped: see the task Notes / STATUS open tasks.
//
// Covered here (task file acceptance 9 and 10, stubbed): three guaranteed stages in order with the active one marked
// `aria-current="step"`, `0 %` + elapsed + the e-mail/leave notice, the waiting-for-slot notice after 30 s with no
// error and no HTTP read per tick, a live `RenderProgress` moving the bar and showing a sub-step without any refetch,
// an unknown stage/over-range percent never rendered raw, the terminal transition through `EpisodeUpdated` + one
// refetch, the `render_quality_failed` copy without the server `detail`, the `image_moderated` scene list with its
// moderated guidance and working `?segment=` link (its fix hint, no action while the episode is failed), vi + en over
// the same states, axe clean in light and dark, 360 px
// without horizontal scroll, `prefers-reduced-motion` without a bar transition, and the D4 CSP / Trusted Types
// detector (the `cspLog` fixture fails the test on any violation, in every state).
//
// Copy is read from the shipped bundles (`src/assets/i18n/**`) and never re-typed, like the other fixtures.

// ---------------------------------------------------------------------------------------------------------------
// Copy
// ---------------------------------------------------------------------------------------------------------------

interface RenderCopy {
  readonly timeline: {
    readonly heading: string;
    readonly percent: string;
    readonly elapsed: string;
    readonly emailNotice: string;
    readonly leave: string;
    readonly announce: string;
    readonly state: Readonly<Record<'done' | 'active' | 'todo' | 'failed', string>>;
  };
  readonly stage: Readonly<Record<'queued' | 'running' | 'ingesting', string>>;
  readonly subStep: Readonly<Record<'voice' | 'subtitles' | 'images' | 'assembling' | 'checking', string>>;
  readonly waiting: { readonly slot: string };
  readonly failure: {
    readonly title: string;
    readonly scenes: string;
    readonly editScenes: string;
    readonly editScenesHint: string;
    readonly retry: string;
    readonly generic: string;
  };
  readonly images: { readonly skippedBanner: string; readonly moderatedScene: string; readonly reason: Readonly<Record<string, string>> };
}

/** The shared `errors` scope of the wave-1 catalogue (README D9): the render outcome codes are not redefined here. */
interface ErrorCopy {
  readonly image_moderated: string;
  readonly render_quality_failed: string;
}

const RENDER_COPY: Readonly<Record<E2eLang, RenderCopy>> = {
  vi: episodesVi.render as unknown as RenderCopy,
  en: episodesEn.render as unknown as RenderCopy,
};
const ERROR_COPY: Readonly<Record<E2eLang, ErrorCopy>> = {
  vi: errorsVi as unknown as ErrorCopy,
  en: errorsEn as unknown as ErrorCopy,
};

/** The stage order 10.6.1-T1 pins (README WC2, `render-stage.GUARANTEED`). */
const GUARANTEED = ['queued', 'running', 'ingesting'] as const;
/** `data-testid` values fixed for 10.10.2-T1 (task Notes / `plan.md` §10 O-3). */
const TID = {
  timeline: 'render-timeline',
  stage: 'render-stage',
  percent: 'render-percent',
  waiting: 'render-waiting',
  failure: 'render-failure',
  scenes: 'render-failure-scenes',
} as const;

// ---------------------------------------------------------------------------------------------------------------
// Wire fixtures
// ---------------------------------------------------------------------------------------------------------------

/** `renderJob` as the wire carries it: 09.5.3's summary plus 03.4.2-T1's `images` and the opaque, never-rendered `params`. */
interface RenderJobFixture extends RenderJobSummaryDto {
  readonly images?: {
    readonly needed: number;
    readonly succeeded: number;
    readonly skipped: readonly { readonly segment: string; readonly reason: string }[];
  };
  readonly params?: Readonly<Record<string, string>>;
}

/** Marker of the `renderJob.params`-like extra property: the view never reads it, so it can never reach the DOM. */
const PARAMS_MARKER = 'RENDER-PARAMS-MUST-NOT-BE-RENDERED';
const RENDER_JOB_ID = '99999999-9999-4999-8999-999999999999';
/** A `submitted_at` in the recent past: the elapsed time then counts from it, never from a client guess (WT53). */
const submittedAt = (): string => new Date(Date.now() - 90_000).toISOString();
const finishedAt = (): string => new Date(Date.now() - 5_000).toISOString();

type RenderState = 'queued' | 'waiting' | 'running' | 'ingesting' | 'succeeded' | 'failedQuality' | 'failedModerated';
/** Every state of the view this task owns, for the axe / 360 px sweeps. */
const STATES: readonly RenderState[] = ['queued', 'running', 'ingesting', 'succeeded', 'failedQuality', 'failedModerated'];
const FAILED_STATES: readonly RenderState[] = ['failedQuality', 'failedModerated'];

function renderJob(overrides: Partial<RenderJobFixture> = {}): RenderJobFixture {
  return {
    renderJobId: RENDER_JOB_ID,
    status: 'queued',
    stage: 'queued',
    percent: 0,
    attempt: 1,
    imagesNeeded: 6,
    submittedAt: null,
    finishedAt: null,
    errorCode: null,
    hints: [],
    params: { render_params: PARAMS_MARKER },
    ...overrides,
  };
}

function detail(status: EpisodeDetailDto['status'], job: RenderJobFixture, nextActions: EpisodeDetailDto['nextActions']): EpisodeDetailDto {
  return episodeDetail('done', { status, generation: null, nextActions, renderJob: job, etag: `"episode-${status}"` });
}

/**
 * One detail payload per state. `renderJob` is `null`-able in the DTO and every optional field is present or absent on
 * purpose (O-2): `submittedAt` is `null` for the whole queued wait (09.6.1-T1 sets it only at `MarkSubmitted`), which
 * is exactly what the client-side `queuedSince` baseline exists for (WB2/WD2).
 */
function stateDetail(state: RenderState): EpisodeDetailDto {
  switch (state) {
    case 'queued':
    case 'waiting':
      return detail('rendering', renderJob(), ['cancel']);
    case 'running':
      return detail('rendering', renderJob({ status: 'running', stage: 'running', percent: 10, submittedAt: submittedAt() }), ['cancel']);
    case 'ingesting':
      return detail('rendering', renderJob({ status: 'ingesting', stage: 'ingesting', percent: 90, submittedAt: submittedAt() }), ['cancel']);
    case 'succeeded':
      return detail('rendering', renderJob({ status: 'succeeded', stage: 'ingesting', percent: 100, submittedAt: submittedAt(), finishedAt: finishedAt() }), []);
    case 'failedQuality':
      return detail('failed', renderJob({
        status: 'failed', stage: 'running', percent: 42, submittedAt: submittedAt(), finishedAt: finishedAt(),
        errorCode: 'render_quality_failed', hints: ['subtitle_sync_suspect'],
      }), ['retry_render', 'cancel']);
    case 'failedModerated':
      return detail('failed', renderJob({
        status: 'failed', stage: 'images', percent: 40, imagesNeeded: 12, submittedAt: submittedAt(), finishedAt: finishedAt(),
        errorCode: 'image_moderated',
        images: { needed: 12, succeeded: 10, skipped: [{ segment: 'p:3', reason: 'moderated' }, { segment: 'hook', reason: 'timeout' }] },
      }), ['retry_render', 'cancel']);
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Stub (session + series + hub ticket + SignalR JSON handshake + episode detail), the episodes-stubs.ts shape
// ---------------------------------------------------------------------------------------------------------------

const RECORD_SEPARATOR = '\u001e';
const TICKET = 'e2e-hub-ticket-not-for-storage';
/** PreferencesStore storage key and shape (core/prefs/preferences.store.ts), the shell/hints spec convention. */
const PREFS_KEY = 'nts.prefs.v1';
const RENDER_PATH = `/episodes/${EPISODE_ID}`;

interface RenderPageStub {
  readonly detailGets: () => number;
  readonly connected: () => number;
  /** The payload the next read serves (the refetch after a terminal `EpisodeUpdated`). */
  readonly serve: (next: EpisodeDetailDto) => void;
  /** One `RenderProgress` frame for this episode's job (README D8/WR7): ids + `data`, never parsed as state. */
  readonly pushProgress: (progress: { readonly stage: string; readonly percent: number }) => void;
  /** One ids-only `EpisodeUpdated` frame: the page refetches and re-derives everything from the detail. */
  readonly pushEpisodeUpdated: () => void;
}

interface OpenOptions {
  /** `page.clock` is required by the 30 s waiting threshold (task file acceptance 9: 29 s / 31 s). */
  readonly clock?: boolean;
  readonly theme?: 'light' | 'dark';
}

async function openRenderPage(page: Page, lang: E2eLang, state: RenderState, options: OpenOptions = {}): Promise<RenderPageStub> {
  if (options.clock === true) {
    // Installed BEFORE the navigation (dashboard.qa.spec.ts precedent), so the store's 1 s tick is a fake timer and
    // `fastForward` moves the elapsed clock deterministically instead of sleeping for real.
    await page.clock.install();
  }
  if (options.theme !== undefined) {
    await page.goto('about:blank');
    await page.addInitScript(
      ([key, value]) => localStorage.setItem(key as string, value as string),
      [PREFS_KEY, JSON.stringify({ uiLanguage: lang, theme: options.theme })],
    );
  }
  await stubSession(page, lang);
  await stubSeriesApi(page, { detail: seriesDetail({ id: SERIES_ID, topic: 'ghost' }) });

  const hub = await stubEpisodeHub(page);
  let current = stateDetail(state);
  let gets = 0;

  await page.route(`**/api/v1/episodes/${EPISODE_ID}`, async (route) => {
    gets += 1;
    await fulfilJson(route, 200, current, { ETag: current.etag });
  });

  await page.goto(RENDER_PATH);
  await expect(page.locator('html')).toHaveAttribute('lang', lang);

  return {
    detailGets: () => gets,
    connected: hub.connected,
    serve: (next) => { current = next; },
    pushProgress: (progress) => hub.push('RenderProgress', { ids: { episodeId: EPISODE_ID, renderJobId: RENDER_JOB_ID }, data: progress }),
    pushEpisodeUpdated: () => hub.push('EpisodeUpdated', { ids: { episodeId: EPISODE_ID, seriesId: SERIES_ID } }),
  };
}

interface EpisodeHubStub {
  readonly connected: () => number;
  /** One ids-plus-arguments hub frame; throws while the JSON handshake has not completed (a lost push would be silent). */
  readonly push: (target: string, payload: unknown) => void;
}

/**
 * The hub stub both page helpers share: the ticket read and a real SignalR JSON handshake over `routeWebSocket`
 * (`stubSession` answers 503 here so no spec connects by accident; these specs drive the real hub path instead).
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
// Locators and copy helpers
// ---------------------------------------------------------------------------------------------------------------

const timeline = (page: Page): Locator => page.getByTestId(TID.timeline);
const stages = (page: Page): Locator => page.getByTestId(TID.stage);
const activeStage = (page: Page): Locator => page.locator(`[data-testid="${TID.stage}"][aria-current="step"]`);
const bar = (page: Page): Locator => timeline(page).getByRole('progressbar');
const barFill = (page: Page): Locator => timeline(page).locator('.progress-fill');
const percentLine = (page: Page): Locator => page.getByTestId(TID.percent);
const waiting = (page: Page): Locator => page.getByTestId(TID.waiting);
const failure = (page: Page): Locator => page.getByTestId(TID.failure);
/** The rendered duration: `render.timeline.elapsed` with `{{duration}}` (the component formats `m:ss` itself). */
const elapsedLine = (page: Page): Locator => timeline(page).locator('.elapsed');

const escapeRegExp = (value: string): string => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const fill = (template: string, values: Readonly<Record<string, string>>): string =>
  Object.entries(values).reduce((text, [key, value]) => text.replace(`{{${key}}}`, value), template);
const percentText = (copy: RenderCopy, percent: number): string => fill(copy.timeline.percent, { percent: String(percent) });
/** The rendered duration line, capturing `m` and `ss` so the view's own clock can be read back. */
const elapsedPattern = (copy: RenderCopy): RegExp => {
  const [before = '', after = ''] = copy.timeline.elapsed.split('{{duration}}');
  return new RegExp(`^${escapeRegExp(before)}(\\d+):(\\d{2})${escapeRegExp(after)}$`);
};
/** The elapsed seconds the view shows: the 30 s threshold is crossed from this, never from the boot instant. */
async function shownElapsedSec(page: Page, copy: RenderCopy): Promise<number> {
  const text = (await elapsedLine(page).innerText()).replace(/\s+/g, ' ').trim();
  const match = elapsedPattern(copy).exec(text);
  if (match === null) throw new Error(`the timeline shows no elapsed time (text: ${text})`);
  return Number(match[1]) * 60 + Number(match[2]);
}
/** The live-region sentence for a stage change (with the sub-step when the adapter reported one). */
const announcement = (copy: RenderCopy, stage: keyof RenderCopy['stage'], subStep?: keyof RenderCopy['subStep']): string => {
  const sentence = fill(copy.timeline.announce, { stage: copy.stage[stage] });
  return subStep === undefined ? sentence : `${sentence} — ${copy.subStep[subStep]}`;
};

declare global {
  interface Window {
    /** Distinct texts of the timeline's polite live region, in order (acceptance 10: one announcement per stage). */
    __ntsRenderAnnouncements?: string[];
  }
}

/**
 * Runs in every document before any script; `document` is observed because `<html>` may not exist yet. The selector is
 * a literal on purpose: an init script is serialised into the page, so a module-scope constant (or any closure) would
 * be undefined there.
 */
function watchAnnouncements(): void {
  const records: string[] = [];
  window.__ntsRenderAnnouncements = records;
  new MutationObserver(() => {
    const region = document.querySelector('[data-testid="render-timeline"] [aria-live]');
    const text = (region?.textContent ?? '').replace(/\s+/g, ' ').trim();
    if (text.length > 0 && records[records.length - 1] !== text) records.push(text);
  }).observe(document, { subtree: true, childList: true, characterData: true });
}
const announcements = (page: Page): Promise<string[]> => page.evaluate(() => window.__ntsRenderAnnouncements ?? []);

async function expectState(page: Page, state: RenderState): Promise<void> {
  if (FAILED_STATES.includes(state)) {
    await expect(failure(page)).toBeVisible();
    return;
  }
  await expect(timeline(page)).toBeVisible();
  await expect(stages(page)).toHaveCount(GUARANTEED.length);
}

/** The stubbed hub handshake has completed, so a push cannot be lost (10.5.2-T1's subscribe-before-read discipline). */
async function expectHub(api: RenderPageStub): Promise<void> {
  await expect.poll(api.connected, { message: 'the stubbed SignalR handshake completes' }).toBe(1);
}

/**
 * Settles the read count a claim of ABSENCE is measured against: opening the page read the detail once, and the
 * connect-time resync (`RealtimeService.resyncAfterConnect` → `notifyAll`) may legitimately re-read it once more right
 * after the socket comes up. Waiting for the stubbed handshake plus one quiet window takes the baseline after that, so
 * each claim below is a *delta*: a clock tick and a `RenderProgress` frame must each add nothing (acceptance 9/10).
 */
async function settleReads(page: Page, api: RenderPageStub): Promise<number> {
  await expectHub(api);
  await page.waitForTimeout(650);
  const settled = api.detailGets();
  expect(settled, 'opening the render view reads the episode detail at least once').toBeGreaterThanOrEqual(1);
  return settled;
}

/**
 * The bounded quiet window that proves the absence, instead of hiding a duplicate or polling read behind a script. Real
 * time on purpose (the app is not being waited on, its extra requests are); the same deliberate window as the
 * generation fixture's `expectNoExtraDetails`.
 */
async function expectNoNewReads(page: Page, api: RenderPageStub, settled: number): Promise<void> {
  await page.waitForTimeout(650);
  expect(api.detailGets(), 'the live view updates in place: no tick and no progress frame reads the episode again').toBe(settled);
}

// ---------------------------------------------------------------------------------------------------------------
// Flows (vi and en run the same states)
// ---------------------------------------------------------------------------------------------------------------

/** Acceptance 9 (stubbed): queued shows three stages in order, `0 %`, the elapsed clock and the leave notice. */
async function queuedFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = RENDER_COPY[lang];
  const api = await openRenderPage(page, lang, 'queued', { clock: true });
  await expectState(page, 'queued');
  const settled = await settleReads(page, api);

  for (const [index, key] of GUARANTEED.entries()) {
    await expect(stages(page).nth(index), `stage ${index}`).toContainText(copy.stage[key]);
  }
  await expect(stages(page).nth(0)).toHaveAttribute('aria-current', 'step');
  await expect(activeStage(page), 'exactly the queued stage is active').toHaveCount(1);
  await expect(activeStage(page)).toContainText(copy.stage.queued);

  // Acceptance 10 (stubbed): the labelled progressbar contract.
  await expect(bar(page)).toHaveAttribute('aria-valuenow', '0');
  await expect(bar(page)).toHaveAttribute('aria-valuemin', '0');
  await expect(bar(page)).toHaveAttribute('aria-valuemax', '100');
  await expect(bar(page)).toHaveAttribute('aria-valuetext', `${copy.stage.queued} — ${percentText(copy, 0)}`);
  await expect(percentLine(page)).toHaveText(percentText(copy, 0));
  await expect(elapsedLine(page)).toHaveText(elapsedPattern(copy));
  await expect(timeline(page)).toContainText(copy.timeline.emailNotice);
  await expect(timeline(page)).toContainText(copy.timeline.leave);
  await expect(waiting(page), 'no waiting notice while the job is still recent').toHaveCount(0);
  await expect(timeline(page).getByRole('alert'), 'the waiting state is never an error (F4)').toHaveCount(0);

  // 29 s of elapsed time: below the threshold the notice must stay absent and the tick must not read.
  // The fake clock ticks with real time, so the advance is computed from the elapsed the view shows instead of
  // assuming boot is "0 s ago" — this keeps the 29 s side of the boundary on the correct side of 30 s.
  const shown = await shownElapsedSec(page, copy);
  await page.clock.fastForward(Math.max(0, (29 - shown) * 1_000));
  await expect(waiting(page), 'below the 30 s threshold the slot notice must stay absent').toHaveCount(0);
  await expect(timeline(page).getByRole('alert')).toHaveCount(0);
  await expect(elapsedLine(page)).toHaveText(elapsedPattern(copy));

  // 31 s: the slot notice appears as text (not colour, not an alert), the error copy never appears.
  await page.clock.fastForward(2_000);
  await expect(waiting(page)).toBeVisible();
  await expect(waiting(page)).toContainText(copy.waiting.slot);
  await expect(timeline(page).getByRole('alert'), 'the slot notice is not an alert').toHaveCount(0);
  await expect(timeline(page)).not.toContainText(copy.failure.title);

  // Neither the 29 s stretch nor the notice itself performed a read (the tick only moves the local clock).
  await expectNoNewReads(page, api, settled);
  await expectA11yClean(page);
}

/** Acceptance 9/10 (stubbed): live progress moves the bar, shows a sub-step, never refetches, never renders raw ids. */
async function runningFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = RENDER_COPY[lang];
  await page.addInitScript(watchAnnouncements);
  const api = await openRenderPage(page, lang, 'running');
  await expectState(page, 'running');
  const settled = await settleReads(page, api);
  await expect(bar(page)).toHaveAttribute('aria-valuenow', '10');
  await expect(stages(page).nth(1)).toHaveAttribute('aria-current', 'step');
  await expect(activeStage(page)).toHaveCount(1);

  api.pushProgress({ stage: 'running', percent: 42 });
  await expect(bar(page)).toHaveAttribute('aria-valuenow', '42');
  await expect(percentLine(page)).toHaveText(percentText(copy, 42));
  await expectNoNewReads(page, api, settled);

  // A finer node sub-step under the active `running` stage, and the bar reset to the event's own percent.
  api.pushProgress({ stage: 'assembling', percent: 55 });
  await expect(bar(page)).toHaveAttribute('aria-valuenow', '55');
  await expect(stages(page).nth(1)).toContainText(copy.subStep.assembling);
  await expect(stages(page).nth(1)).toHaveAttribute('aria-current', 'step');
  await expectNoNewReads(page, api, settled);

  // Acceptance 10: one announcement per STAGE change, not one per percent frame (the elapsed ticks and the 42 % frame
  // in between must not re-announce). Read before the unknown-stage frame below, which legitimately reverts the
  // sub-step text.
  expect(await announcements(page)).toEqual([
    announcement(copy, 'running'),
    announcement(copy, 'running', 'assembling'),
  ]);

  // 10.2.4-T1's policy: an unknown stage is dropped and an out-of-range percent is clamped, so neither is ever raw.
  api.pushProgress({ stage: 'v2', percent: 250 });
  await expect(timeline(page)).not.toContainText('v2');
  await expect(percentLine(page)).not.toHaveText(percentText(copy, 250));
  const shown = Number(await bar(page).getAttribute('aria-valuenow'));
  expect(Number.isFinite(shown) && shown <= 100, `the bar never exceeds 100 (got ${shown})`).toBe(true);
  await expectNoNewReads(page, api, settled);
  await expectA11yClean(page);
}

/** Acceptance 9 (stubbed): ingesting is active, and the terminal state arrives through `EpisodeUpdated` + one refetch. */
async function ingestingTerminalFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = RENDER_COPY[lang];
  const api = await openRenderPage(page, lang, 'ingesting');
  await expectState(page, 'ingesting');
  const settled = await settleReads(page, api);
  await expect(stages(page).nth(2)).toHaveAttribute('aria-current', 'step');
  await expect(activeStage(page)).toHaveCount(1);

  // The episode stays `rendering` on purpose: the `video_review` branch that a produced video leads to is 10.6.2-T1's,
  // so this task proves the transition it owns — a push, one refetch, a view re-derived from the fresh detail.
  api.serve(stateDetail('succeeded'));
  api.pushEpisodeUpdated();
  await expect.poll(api.detailGets, { message: 'one refetch per EpisodeUpdated' }).toBe(settled + 1);
  await expect(stages(page)).toHaveCount(GUARANTEED.length);
  for (const [index] of GUARANTEED.entries()) {
    await expect(stages(page).nth(index), `stage ${index} is done once the job succeeded`).toContainText(copy.timeline.state.done);
  }
  await expect(activeStage(page), 'no stage stays active after the terminal status').toHaveCount(0);
  await expectNoNewReads(page, api, settled + 1);
  await expectA11yClean(page);
}

/**
 * Acceptance 9 (stubbed): `render_quality_failed` renders the shared `errors.<code>` title, hides the bar, and the
 * panel's retry is the live affordance 10.6.1-T2 binds — this stub's `nextActions` offer `retry_render`, and the
 * command behind that click is the one the T2 flows below exercise end to end.
 */
async function failedQualityFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = RENDER_COPY[lang];
  const errors = ERROR_COPY[lang];
  const api = await openRenderPage(page, lang, 'failedQuality');
  await expectState(page, 'failedQuality');
  const settled = await settleReads(page, api);
  await expect(failure(page)).toHaveAttribute('role', 'alert');
  await expect(failure(page).getByRole('heading', { level: 2 })).toHaveText(errors.render_quality_failed);
  await expect(percentLine(page), 'a failed job shows no percentage').toHaveCount(0);
  await expect(bar(page), 'a completed-looking bar is never shown next to a failure').toHaveCount(0);

  const retry = failure(page).getByRole('button', { name: copy.failure.retry, exact: true });
  await expect(retry, 'one retry affordance').toHaveCount(1);
  await expect(retry, 'the server offers retry_render, so the retry is live (10.6.1-T2)').toBeEnabled();

  // F20/D9: the server's own text and the opaque job params are never rendered.
  await expect(page.locator('body')).not.toContainText(PARAMS_MARKER);
  await expectNoNewReads(page, api, settled);
  await expectA11yClean(page);
}

/**
 * Acceptance 9 (stubbed): `image_moderated` lists both skipped scenes, links a parseable address, shows the fix hint,
 * and offers no action while the episode is `failed`.
 */
async function failedModeratedFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = RENDER_COPY[lang];
  const errors = ERROR_COPY[lang];
  const api = await openRenderPage(page, lang, 'failedModerated');
  await expectState(page, 'failedModerated');
  const settled = await settleReads(page, api);
  await expect(failure(page).getByRole('heading', { level: 2 })).toHaveText(errors.image_moderated);

  const scenes = page.getByTestId(TID.scenes);
  await expect(scenes).toBeVisible();
  await expect(scenes.getByRole('heading', { level: 3 })).toHaveText(copy.failure.scenes);
  const rows = scenes.getByRole('listitem');
  await expect(rows, 'both skipped scenes are listed').toHaveCount(2);

  const moderatedRow = rows.filter({ hasText: 'p:3' });
  await expect(moderatedRow).toContainText(copy.images.reason.moderated);
  await expect(moderatedRow, 'the moderated row carries the guidance').toContainText(copy.images.moderatedScene);
  const timedOutRow = rows.filter({ hasText: 'hook' });
  await expect(timedOutRow).toContainText(copy.images.reason.timeout);
  await expect(timedOutRow, 'only the moderated row carries the guidance').not.toContainText(copy.images.moderatedScene);

  // 10.5.3-T1's `?segment=` deep link; the query value is parsed instead of string-compared (encoding is Angular's).
  const link = scenes.getByRole('link', { name: 'p:3', exact: true });
  await expect(link).toHaveCount(1);
  const target = new URL((await link.getAttribute('href')) ?? '', page.url());
  expect(target.pathname, 'the scene link points at this episode').toBe(`/episodes/${EPISODE_ID}`);
  expect(target.searchParams.get('segment'), 'the scene link carries the canonical address').toBe('p:3');

  // A failed episode's nextActions are retry_render + cancel (EpisodeNextActions.cs:118): the panel renders the hint
  // and the scene list, and no edit action (task file failure-handling row: "button absent").
  await expect(failure(page)).toContainText(copy.failure.editScenesHint);
  await expect(failure(page).getByRole('button', { name: copy.failure.editScenes, exact: true })).toHaveCount(0);
  const retry = failure(page).getByRole('button', { name: copy.failure.retry, exact: true });
  if ((await retry.count()) > 0) expect(await retry.isEnabled(), 'retry is not offered by this task').toBe(false);
  await expect(page.locator('body')).not.toContainText(PARAMS_MARKER);
  await expectNoNewReads(page, api, settled);
  await expectA11yClean(page);
}

/** Acceptance 10 (stubbed): axe clean in both themes on every standing state (light and dark tokens only). */
async function axeFlow(page: Page, lang: E2eLang): Promise<void> {
  for (const theme of ['light', 'dark'] as const) {
    for (const state of STATES) {
      await openRenderPage(page, lang, state, { theme });
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      await expectState(page, state);
      await expect(page.locator('body'), 'opaque job params never reach the DOM in any state').not.toContainText(PARAMS_MARKER);
      await expectA11yClean(page);
      await page.unrouteAll({ behavior: 'ignoreErrors' });
    }
  }
}

/** Acceptance 10 (stubbed): the waiting state itself is axe clean in both themes (task file names it explicitly). */
async function axeWaitingFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = RENDER_COPY[lang];
  await page.clock.install();
  for (const theme of ['light', 'dark'] as const) {
    await openRenderPage(page, lang, 'waiting', { theme });
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    await expectState(page, 'waiting');
    await page.clock.fastForward(31_000);
    await expect(waiting(page)).toContainText(copy.waiting.slot);
    await expectA11yClean(page);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
}

/** Acceptance 10 (stubbed): 360 px with no horizontal scroll on every state. */
async function narrow360Flow(page: Page, lang: E2eLang): Promise<void> {
  await page.setViewportSize({ width: 360, height: 740 });
  await page.clock.install();
  for (const state of [...STATES, 'waiting' as const]) {
    await openRenderPage(page, lang, state);
    await expectState(page, state);
    if (state === 'waiting') {
      await page.clock.fastForward(31_000);
      await expect(waiting(page)).toBeVisible();
    }
    await expectNoHorizontalScroll(page);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
}

/** Acceptance 10 (stubbed): `prefers-reduced-motion` disables the bar animation (control: it is on without it). */
async function reducedMotionFlow(page: Page, lang: E2eLang): Promise<void> {
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await openRenderPage(page, lang, 'running');
  await expectState(page, 'running');
  await expect(barFill(page)).toBeVisible();
  const allowed = await barFill(page).evaluate((node) => getComputedStyle(node).transitionDuration);
  expect(allowed, 'control: the bar animates when motion is allowed').not.toBe('0s');

  await page.emulateMedia({ reducedMotion: 'reduce' });
  const reduced = await barFill(page).evaluate((node) => getComputedStyle(node).transitionDuration);
  expect(reduced, 'the bar fill transition is disabled under reduced motion').toBe('0s');
  await expectA11yClean(page);
}

// ---------------------------------------------------------------------------------------------------------------
// 10.6.1-T2 — "Make video" and "Cancel render" (task file acceptance 2-11, STUBBED like the timeline above)
// ---------------------------------------------------------------------------------------------------------------
//
// Same owner decision as 10.6.1-T1: this lane runs no compose stack and no mock-mpt, so the two commands
// (`POST /episodes/{id}/render`, `DELETE /render-jobs/{id}`) are stubbed here and the real-stack run of the task file's
// e2e bullet stays open for 11.5.1-T1's `e2e.yml`. The assertions read `request.method()` / `request.headers()` and the
// query-free pathname only: a body is never read, and a key is compared in-test, never printed.

/** The make-video copy of a language (`episodes.render.make`). */
interface MakeCopy {
  readonly action: string;
  readonly retry: string;
  readonly confirm: { readonly title: string; readonly body: string };
  readonly blockedKeywords: string;
  readonly inProgress: string;
}

/** The cancel copy (`episodes.render.cancel`). */
interface CancelCopy {
  readonly action: string;
  readonly title: string;
  readonly body: string;
  readonly confirm: string;
  readonly busy: string;
}

/** The `errors` leaves these flows render (D9: the server's `detail` prose is never read, only its code is). */
interface ActionErrorCopy {
  readonly keywords_flagged: string;
  readonly episode_invalid_state: string;
  readonly episode_conflict: string;
  readonly concurrency_conflict: string;
  readonly spend_cap_reached: string;
  readonly network_error: string;
  readonly not_found: string;
}

/** The root-scope copy the page itself renders: the dialog's cancel label and the status chip's labels. */
interface CoreActionCopy {
  readonly dialogCancel: string;
  readonly status: Readonly<Record<'rendering' | 'story_approved' | 'cancelled', string>>;
}

const MAKE_COPY: Readonly<Record<E2eLang, MakeCopy>> = {
  vi: episodesVi.render.make as unknown as MakeCopy,
  en: episodesEn.render.make as unknown as MakeCopy,
};
const CANCEL_COPY: Readonly<Record<E2eLang, CancelCopy>> = {
  vi: episodesVi.render.cancel as unknown as CancelCopy,
  en: episodesEn.render.cancel as unknown as CancelCopy,
};
const ACTION_ERRORS: Readonly<Record<E2eLang, ActionErrorCopy>> = {
  vi: errorsVi as unknown as ActionErrorCopy,
  en: errorsEn as unknown as ActionErrorCopy,
};
/** `errors.keywordsFlaggedRule.<rule>`: the note the flagged chip points at (10.5.10-T1 owns the chips). */
const FLAG_RULES: Readonly<Record<E2eLang, Readonly<Record<string, string>>>> = {
  vi: errorsVi.keywordsFlaggedRule as unknown as Readonly<Record<string, string>>,
  en: errorsEn.keywordsFlaggedRule as unknown as Readonly<Record<string, string>>,
};
const CORE_ACTION: Readonly<Record<E2eLang, CoreActionCopy>> = {
  vi: { dialogCancel: coreVi.core.ui.confirm.cancel, status: coreVi.core.ui.statusChip },
  en: { dialogCancel: coreEn.core.ui.confirm.cancel, status: coreEn.core.ui.statusChip },
};

const RENDER_POST_PATH = `/api/v1/episodes/${EPISODE_ID}/render`;
const RENDER_JOB_PATH = `/api/v1/render-jobs/${RENDER_JOB_ID}`;
/** The 409 row the task file pins: the fourth paragraph's second keyword carries a name. */
const FLAG_ROW = { segment: 'p:3', index: 1, rule: 'keyword_name_detected' } as const;
const FLAG_ANNOTATION_ID = `ann-p-3-${FLAG_ROW.rule}-k${FLAG_ROW.index}`;
/** The server's own prose: it is never rendered (D9). */
const SERVER_DETAIL = 'PRIVATE SERVER DETAIL MUST NOT BE RENDERED';
/** `crypto.randomUUID()` (10.2.3-T1); the API allows 1-128 printable ASCII, so this is the exact shape expected. */
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
/** D14/F19: no credit, price or cost string anywhere in these flows. */
const COST_TEXT = /credit|tín dụng|cost|giá/i;
/** The job statuses the server answers 409 for, so no cancel affordance exists for them. */
const CLOSED_STATES: readonly string[] = ['ingesting', 'succeeded', 'failed', 'cancelled'];

/**
 * The approved story the make-video flows run on: `STORY_CONTENT` with a second keyword on the fourth paragraph
 * (`p:3`), so the flagged chip index the task file pins addresses a keyword that exists.
 */
const ACTION_STORY: StoryWire = {
  ...STORY_CONTENT,
  paragraphs: STORY_CONTENT.paragraphs.map((paragraph, index) =>
    (index === 3 ? { ...paragraph, keywords: [...paragraph.keywords, 'ngoài sân'] } : paragraph)),
};

/** An approved episode the server lets render: `nextActions` carries `render`, and the story is loaded for the editor. */
function approvedDetail(overrides: Partial<EpisodeDetailDto> = {}): EpisodeDetailDto {
  return episodeDetail('done', {
    title: STORY_CONTENT.title, status: 'story_approved', etag: '"episode-approved"', nextActions: ['render'],
    currentStory: storySnapshot(ACTION_STORY), generation: null, renderJob: null,
    ...overrides,
  });
}

/** A `rendering` episode whose job carries a chosen status: the cancel affordance's own gate. */
function jobDetail(status: string, nextActions: EpisodeDetailDto['nextActions'] = ['cancel']): EpisodeDetailDto {
  const active = ['queued', 'running', 'ingesting'].includes(status);
  return detail('rendering', renderJob({
    status, stage: active ? status : 'ingesting', percent: active ? 10 : 100, submittedAt: submittedAt(),
    finishedAt: active ? null : finishedAt(),
  }), nextActions);
}

interface CapturedCommand {
  readonly method: string;
  /** Query-free pathname only: the key rides in a header, so no recorded URL ever carries one. */
  readonly path: string;
  readonly ifMatch: string | null;
  readonly idempotencyKey: string | null;
}

type SubmitReply =
  | { readonly kind: 'accepted' }
  | { readonly kind: 'abort' }
  | { readonly kind: 'problem'; readonly status: number; readonly code: string; readonly errors?: readonly unknown[]; readonly retryAfter?: number };
type CancelReply = { readonly kind: 'noContent' } | { readonly kind: 'problem'; readonly status: number; readonly code: string };

interface ActionStub {
  readonly detailGets: () => number;
  readonly connected: () => number;
  readonly submits: readonly CapturedCommand[];
  readonly cancels: readonly CapturedCommand[];
  /** The answers of the POSTs, in order (the last one repeats once the script is exhausted). */
  readonly replySubmit: (...replies: readonly SubmitReply[]) => void;
  readonly replyCancel: (...replies: readonly CancelReply[]) => void;
  /** Holds the next command's response so the in-flight (busy) state can be inspected; `release` answers it. */
  readonly holdSubmit: () => void;
  readonly releaseSubmit: () => void;
  readonly holdCancel: () => void;
  readonly releaseCancel: () => void;
  readonly serve: (next: EpisodeDetailDto) => void;
  readonly pushEpisodeUpdated: () => void;
}

interface ActionOptions {
  /** The detail the reads serve until `serve()` changes it (the first read serves it too). */
  readonly detail: EpisodeDetailDto;
  readonly clock?: boolean;
  readonly theme?: 'light' | 'dark';
}

/** A one-shot gate: the next held request waits until `release()`, which is what the busy states are observed on. */
function createGate(): { readonly start: () => void; readonly release: () => void; readonly wait: () => Promise<void> } {
  let resolveGate: (() => void) | null = null;
  let pending: Promise<void> | null = null;
  return {
    start: () => { pending = new Promise<void>((resolve) => { resolveGate = resolve; }); },
    release: () => { resolveGate?.(); resolveGate = null; pending = null; },
    wait: async () => { if (pending !== null) await pending; },
  };
}

/**
 * The episode page with the two render commands stubbed: session + series + hub as the timeline flows, the detail read
 * with a mutable payload and a counter (the refetches are what acceptance 5-9 assert), and scripted answers for
 * `POST /episodes/{id}/render` (`If-Match` + one caller-owned `Idempotency-Key`, 09.6.1-T1) and
 * `DELETE /render-jobs/{id}` (no headers at all, 09.6.4-T1).
 */
async function openActionPage(page: Page, lang: E2eLang, options: ActionOptions): Promise<ActionStub> {
  if (options.clock === true) await page.clock.install();
  if (options.theme !== undefined) {
    await page.goto('about:blank');
    await page.addInitScript(
      ([key, value]) => localStorage.setItem(key as string, value as string),
      [PREFS_KEY, JSON.stringify({ uiLanguage: lang, theme: options.theme })],
    );
  }
  await stubSession(page, lang);
  await stubSeriesApi(page, { detail: seriesDetail({ id: SERIES_ID, topic: 'ghost' }) });
  const hub = await stubEpisodeHub(page);

  let current = options.detail;
  let gets = 0;
  const submits: CapturedCommand[] = [];
  const cancels: CapturedCommand[] = [];
  let submitReplies: readonly SubmitReply[] = [];
  let cancelReplies: readonly CancelReply[] = [];
  const submitGate = createGate();
  const cancelGate = createGate();
  const capture = (url: string, headers: Record<string, string>, method: string): CapturedCommand => ({
    method,
    path: new URL(url).pathname,
    ifMatch: headers['if-match'] ?? null,
    idempotencyKey: headers['idempotency-key'] ?? null,
  });

  await page.route((url) => url.pathname === RENDER_POST_PATH, async (route) => {
    const request = route.request();
    submits.push(capture(request.url(), request.headers(), request.method()));
    await submitGate.wait();
    const reply: SubmitReply = submitReplies[Math.min(submits.length - 1, submitReplies.length - 1)] ?? { kind: 'accepted' };
    if (reply.kind === 'abort') return route.abort('failed');
    if (reply.kind === 'problem') {
      return fulfilJson(route, reply.status, problem(reply.status, reply.code, {
        detail: SERVER_DETAIL, ...(reply.errors === undefined ? {} : { errors: reply.errors }),
      }), {
        'Content-Type': 'application/problem+json',
        ...(reply.retryAfter === undefined ? {} : { 'Retry-After': String(reply.retryAfter) }),
      });
    }
    // 202 + `Location: /render-jobs/{id}` (09.6.1-T1); the repository parses the id out of the header.
    return fulfilJson(route, 202, { renderJobId: RENDER_JOB_ID, status: 'queued' }, { Location: `/render-jobs/${RENDER_JOB_ID}` });
  });
  await page.route((url) => url.pathname === RENDER_JOB_PATH, async (route) => {
    const request = route.request();
    cancels.push(capture(request.url(), request.headers(), request.method()));
    await cancelGate.wait();
    const reply: CancelReply = cancelReplies[Math.min(cancels.length - 1, cancelReplies.length - 1)] ?? { kind: 'noContent' };
    if (reply.kind === 'problem') {
      return fulfilJson(route, reply.status, problem(reply.status, reply.code, { detail: SERVER_DETAIL }), { 'Content-Type': 'application/problem+json' });
    }
    return route.fulfill({ status: 204 });
  });
  await page.route((url) => url.pathname === `/api/v1/episodes/${EPISODE_ID}`, async (route) => {
    gets += 1;
    await fulfilJson(route, 200, current, { ETag: current.etag });
  });

  await page.goto(RENDER_PATH);
  await expect(page.locator('html')).toHaveAttribute('lang', lang);

  return {
    detailGets: () => gets,
    connected: hub.connected,
    submits,
    cancels,
    replySubmit: (...replies) => { submitReplies = replies; },
    replyCancel: (...replies) => { cancelReplies = replies; },
    holdSubmit: submitGate.start,
    releaseSubmit: submitGate.release,
    holdCancel: cancelGate.start,
    releaseCancel: cancelGate.release,
    serve: (next) => { current = next; },
    pushEpisodeUpdated: () => hub.push('EpisodeUpdated', { ids: { episodeId: EPISODE_ID, seriesId: SERIES_ID } }),
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Action flows (vi and en run the same states)
// ---------------------------------------------------------------------------------------------------------------

const makeButton = (page: Page): Locator => page.getByTestId('make-video');
const cancelButton = (page: Page): Locator => page.getByTestId('render-cancel');
/** The one refusal line the page shows for a refused command (D9): the server's `detail` is never rendered. */
const makeError = (page: Page): Locator => page.locator('.story-review-page__make-error');
const cancelError = (page: Page): Locator => page.locator('.cancel-error');
const dialog = (page: Page): Locator => page.getByRole('dialog');
const statusChip = (page: Page): Locator => page.locator('nts-status-chip');
/** The dialog's own buttons: scoping to the dialog keeps a page control with the same label out of the way. */
const dialogButton = (page: Page, label: string): Locator => dialog(page).getByRole('button', { name: label, exact: true });

/** One click → one confirm → the label of the dialog's confirm button. Nothing is sent before the confirm lands. */
async function confirmWith(page: Page, trigger: Locator, label: string): Promise<void> {
  await trigger.click();
  await expect(dialog(page)).toBeVisible();
  await dialogButton(page, label).click();
  await expect(dialog(page)).toBeHidden();
}

/** The button, the failure panel's retry and the dialog all run one command: the confirm's own label is the make label. */
const requestRender = (page: Page, trigger: Locator, lang: E2eLang): Promise<void> => confirmWith(page, trigger, MAKE_COPY[lang].action);

/**
 * The read count a claim of ABSENCE is measured against: the stub handshake plus one quiet window, so a refusal's
 * refetch is a delta and no boot read (or connect-time resync) can be mistaken for it.
 */
async function settleActionReads(page: Page, api: ActionStub): Promise<number> {
  await expect.poll(api.connected, { message: 'the stubbed SignalR handshake completes' }).toBe(1);
  await page.waitForTimeout(650);
  const settled = api.detailGets();
  expect(settled, 'opening the page reads the episode detail at least once').toBeGreaterThanOrEqual(1);
  return settled;
}

/** No further read inside a bounded quiet window: a refusal refetches once, never in a loop. */
async function expectNoFurtherReads(page: Page, api: ActionStub, settled: number): Promise<void> {
  await page.waitForTimeout(650);
  expect(api.detailGets(), 'exactly one refetch per refusal, and none after it').toBe(settled);
}

/** Acceptance 3/4 (stubbed): one confirm, one `POST` with the loaded ETag and a UUID key, no optimistic status. */
async function makeVideoHappyPath(page: Page, lang: E2eLang): Promise<void> {
  const copy = MAKE_COPY[lang];
  const approved = approvedDetail();
  const api = await openActionPage(page, lang, { detail: approved });
  const make = makeButton(page);
  await expect(make).toBeVisible();
  await expect(make).toBeEnabled();
  await expect(make).toHaveText(copy.action);
  await expect(timeline(page), 'an approved episode with no job shows no timeline').toHaveCount(0);

  // The response is held, so the in-flight state is inspected between the click and the answer.
  api.holdSubmit();
  await make.click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialog(page), 'the confirm is a real dialog').toHaveAttribute('role', 'dialog');
  await expect(dialog(page)).toContainText(copy.confirm.title);
  await expect(dialog(page)).toContainText(copy.confirm.body);
  expect((await dialog(page).innerText()).match(COST_TEXT), 'D14: no credit/price/cost text in the confirm').toBeNull();
  await dialogButton(page, copy.action).click();
  await expect(dialog(page)).toBeHidden();

  // AC3: `submitting` disables the button; a further activation issues nothing (a synthetic click bypasses `disabled`).
  await expect(make).toBeDisabled();
  await expect(make).toHaveAttribute('aria-busy', 'true');
  await expect(make).toHaveText(copy.inProgress);
  await make.dispatchEvent('click');
  await expect.poll(() => api.submits.length, { message: 'the request is in flight' }).toBe(1);
  expect(api.submits, 'a second activation while submitting issues no second request').toHaveLength(1);
  api.releaseSubmit();

  // The request itself: exactly one POST, the loaded ETag, a UUID key (compared in-test, never printed).
  await expect.poll(() => api.submits.length).toBe(1);
  const post = api.submits[0]!;
  expect(post.method).toBe('POST');
  expect(post.path).toBe(RENDER_POST_PATH);
  expect(post.ifMatch, "the POST carries the episode's own ETag").toBe(approved.etag);
  expect(post.idempotencyKey ?? '', 'the key is a non-empty UUID').toMatch(UUID_PATTERN);
  expect((post.idempotencyKey ?? '').length, 'the key is at most 128 characters').toBeLessThanOrEqual(128);

  // AC4: the 202 wrote no status — the read still serves the approved episode and the view is unchanged.
  await expect.poll(api.detailGets, { message: 'the 202 triggers the read that decides the view' }).toBeGreaterThan(1);
  await expect(timeline(page), 'no timeline before the server reports the job').toHaveCount(0);
  await expect(make).toBeVisible();
  await expect(make).toBeEnabled();
  await expect(makeError(page), 'an accepted 202 had a usable Location, so there is no error copy').toHaveCount(0);
  await expect(statusChip(page)).toContainText(CORE_ACTION[lang].status.story_approved);

  // AC4: the episode's own status drives the switch, once the read reports the job.
  api.serve(stateDetail('queued'));
  api.pushEpisodeUpdated();
  await expect(timeline(page)).toBeVisible();
  await expect(make, 'the render branch replaces the approved view').toHaveCount(0);
  await expect(statusChip(page)).toContainText(CORE_ACTION[lang].status.rendering);
  await expectA11yClean(page);
}

/** Acceptance 2/3 (stubbed): the affordance matrix, the retry label, and the failure panel's own retry. */
async function makeVideoAffordanceFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = MAKE_COPY[lang];

  // (a) Neither `render` nor `retry_render`: the button is absent, not merely disabled.
  await openActionPage(page, lang, { detail: approvedDetail({ nextActions: ['reopen_story'] }) });
  await expect(makeButton(page), 'the server decides whether the action exists at all').toHaveCount(0);
  await page.unrouteAll({ behavior: 'ignoreErrors' });

  // (b) Only `retry_render` on an approved story: the retry label, and the same confirm + POST underneath.
  const retryDetail = approvedDetail({ nextActions: ['retry_render'] });
  const retryApi = await openActionPage(page, lang, { detail: retryDetail });
  const make = makeButton(page);
  await expect(make).toBeVisible();
  await expect(make).toBeEnabled();
  await expect(make).toHaveText(copy.retry);
  await requestRender(page, make, lang);
  await expect.poll(() => retryApi.submits.length).toBe(1);
  expect(retryApi.submits[0]!.ifMatch).toBe(retryDetail.etag);
  expect(retryApi.submits[0]!.idempotencyKey ?? '').toMatch(UUID_PATTERN);
  await page.unrouteAll({ behavior: 'ignoreErrors' });

  // (c) A failed episode with a job: the failure panel's retry runs the same command (its `nextActions` say `retry_render`).
  const failed = stateDetail('failedQuality');
  const failedApi = await openActionPage(page, lang, { detail: failed });
  await expect(failure(page)).toBeVisible();
  const retry = failure(page).getByRole('button', { name: RENDER_COPY[lang].failure.retry, exact: true });
  await expect(retry).toBeEnabled();
  await requestRender(page, retry, lang);
  await expect.poll(() => failedApi.submits.length).toBe(1);
  expect(failedApi.submits[0]!.method).toBe('POST');
  expect(failedApi.submits[0]!.path).toBe(RENDER_POST_PATH);
  expect(failedApi.submits[0]!.ifMatch).toBe(failed.etag);
  expect(failedApi.submits[0]!.idempotencyKey ?? '').toMatch(UUID_PATTERN);
  await expect(failure(page)).not.toContainText(SERVER_DETAIL);
  await page.unrouteAll({ behavior: 'ignoreErrors' });

  // (d) The same panel, but the server's `nextActions` no longer offer `retry_render`: the entitlement is read, and a
  // refused render is never offered again behind a live-looking button.
  const notOffered: EpisodeDetailDto = { ...stateDetail('failedQuality'), nextActions: ['cancel'] };
  await openActionPage(page, lang, { detail: notOffered });
  const blocked = failure(page).getByRole('button', { name: RENDER_COPY[lang].failure.retry, exact: true });
  await expect(blocked).toBeVisible();
  await expect(blocked, 'a render the server no longer offers is never retried').toBeDisabled();
}

/** Acceptance 5 + the failure table (stubbed): the key survives an unknown outcome, a terminal answer settles it. */
async function makeVideoIdempotencyFlow(page: Page, lang: E2eLang): Promise<void> {
  const cancels = CANCEL_COPY[lang];
  const approved = approvedDetail();
  const api = await openActionPage(page, lang, { detail: approved });
  const make = makeButton(page);

  // The response is lost: the outcome is UNKNOWN, so the intent stays open and nothing is painted as rendering.
  api.replySubmit({ kind: 'abort' });
  await requestRender(page, make, lang);
  await expect(makeError(page)).toContainText(ACTION_ERRORS[lang].network_error);
  expect(api.submits, 'one click is one request').toHaveLength(1);
  const firstKey = api.submits[0]!.idempotencyKey ?? '';
  expect(firstKey, 'the first attempt carried a key').not.toBe('');
  await expect(timeline(page), 'a lost response never becomes a rendered job').toHaveCount(0);

  // The retry of that click replays the SAME key (the server would otherwise start a second job and reserve twice).
  api.replySubmit({ kind: 'accepted' });
  await requestRender(page, make, lang);
  await expect.poll(() => api.submits.length).toBe(2);
  expect(api.submits[1]!.idempotencyKey, 'the retry replays the same logical command').toBe(firstKey);

  // The 202 is terminal for that key: the timeline takes over, and a cancel returns the episode to the approved view.
  api.serve(stateDetail('queued'));
  api.pushEpisodeUpdated();
  await expect(timeline(page)).toBeVisible();
  const cancel = cancelButton(page);
  await expect(cancel).toBeVisible();
  await cancel.click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialog(page)).toContainText(cancels.title);
  await dialogButton(page, cancels.confirm).click();
  await expect(dialog(page)).toBeHidden();
  await expect.poll(() => api.cancels.length).toBe(1);
  api.serve(approvedDetail());
  api.pushEpisodeUpdated();
  await expect(makeButton(page)).toBeVisible();

  // A genuinely new click mints a NEW key, so the server can create a real second job (09.6.1-T1:197).
  api.replySubmit({ kind: 'accepted' });
  await requestRender(page, makeButton(page), lang);
  await expect.poll(() => api.submits.length).toBe(3);
  const thirdKey = api.submits[2]!.idempotencyKey ?? '';
  expect(thirdKey, 'a new click after a terminal answer sends a different key').not.toBe(firstKey);
  expect(thirdKey).toMatch(UUID_PATTERN);
}

/** Acceptance 6 (stubbed): the 409 hands the flags to the editor store, and the button is blocked in place. */
async function makeVideoKeywordsFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = MAKE_COPY[lang];
  const errors = ACTION_ERRORS[lang];
  const api = await openActionPage(page, lang, { detail: approvedDetail() });
  api.replySubmit({ kind: 'problem', status: 409, code: 'keywords_flagged', errors: [FLAG_ROW] });
  await requestRender(page, makeButton(page), lang);

  // D9: the catalogue leaf, once, and never the server's own prose.
  await expect(makeError(page)).toContainText(errors.keywords_flagged);
  await expect(page.getByRole('alert').filter({ hasText: errors.keywords_flagged }), 'one refusal is announced once').toHaveCount(1);
  await expect(page.locator('body')).not.toContainText(SERVER_DETAIL);
  expect((await makeError(page).innerText()).match(COST_TEXT), 'D14: no credit/price/cost text').toBeNull();

  // D16: the button stays present and disabled, with visible text (linked by `aria-describedby`) saying why.
  const make = makeButton(page);
  await expect(make).toBeVisible();
  await expect(make).toBeDisabled();
  const reasonId = await make.getAttribute('aria-describedby');
  expect(reasonId, 'the reason is linked, never tooltip-only').toBeTruthy();
  const reason = page.locator(`#${reasonId}`);
  await expect(reason).toBeVisible();
  await expect(reason).toHaveText(copy.blockedKeywords);

  // WR6: the offending segment is the editor store's flag — the chip is marked and its rule note is readable.
  const chip = segmentGroup(page, EDITOR_COPY[lang].paragraph(4)).locator('.segment-chips__item--flagged');
  await expect(chip, 'the flagged segment lights up in the editor').toHaveCount(1);
  const input = chip.getByRole('textbox');
  await expect(input).toHaveAttribute('aria-invalid', 'true');
  await expect(input).toHaveAttribute('aria-describedby', FLAG_ANNOTATION_ID);
  await expect(page.locator(`#${FLAG_ANNOTATION_ID}`)).toContainText(FLAG_RULES[lang][FLAG_ROW.rule]!);
  await expect(page.locator('body')).not.toContainText(PARAMS_MARKER);
  await expectA11yClean(page);
}

/** Acceptance 7 + the 412/429 rows (stubbed): one refetch per refusal, the mapped copy, no server prose. */
async function makeVideoRefusalFlow(page: Page, lang: E2eLang): Promise<void> {
  const errors = ACTION_ERRORS[lang];

  // 409 `episode_invalid_state`: the episode moved on — one read, the mapped copy, no retry of a refused command.
  {
    const api = await openActionPage(page, lang, { detail: approvedDetail() });
    const settled = await settleActionReads(page, api);
    api.replySubmit({ kind: 'problem', status: 409, code: 'episode_invalid_state' });
    await requestRender(page, makeButton(page), lang);
    await expect(makeError(page)).toContainText(errors.episode_invalid_state);
    await expect.poll(() => api.detailGets(), { message: 'the refusal refetches the episode once' }).toBe(settled + 1);
    await expectNoFurtherReads(page, api, settled + 1);
    await expect(page.locator('body')).not.toContainText(SERVER_DETAIL);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }

  // 409 `concurrency_conflict`: the fresh `nextActions` offer `cancel` instead of `render` (F8), so the button goes.
  {
    const api = await openActionPage(page, lang, { detail: approvedDetail() });
    const settled = await settleActionReads(page, api);
    api.serve(approvedDetail({ nextActions: ['cancel'], renderJob: renderJob({ status: 'queued' }) }));
    api.replySubmit({ kind: 'problem', status: 409, code: 'concurrency_conflict' });
    await requestRender(page, makeButton(page), lang);
    await expect(makeError(page)).toContainText(errors.concurrency_conflict);
    await expect.poll(() => api.detailGets()).toBe(settled + 1);
    await expect(makeButton(page), 'the server no longer offers the render').toHaveCount(0);
    await expect(page.locator('body')).not.toContainText(SERVER_DETAIL);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }

  // 412 `episode_conflict`: one read, then the retry on the NEW ETag (and a new key — the refusal settled the old one).
  {
    const api = await openActionPage(page, lang, { detail: approvedDetail() });
    const settled = await settleActionReads(page, api);
    const fresh = approvedDetail({ etag: '"episode-approved-2"' });
    api.serve(fresh);
    api.replySubmit({ kind: 'problem', status: 412, code: 'episode_conflict' }, { kind: 'accepted' });
    await requestRender(page, makeButton(page), lang);
    await expect(makeError(page)).toContainText(errors.episode_conflict);
    await expect.poll(() => api.detailGets()).toBe(settled + 1);
    const make = makeButton(page);
    await expect(make, 'the retry is offered on the fresh ETag').toBeEnabled();
    await requestRender(page, make, lang);
    await expect.poll(() => api.submits.length).toBe(2);
    expect(api.submits[1]!.ifMatch, 'the retry carries the ETag the refetch loaded').toBe(fresh.etag);
    expect(api.submits[1]!.idempotencyKey).not.toBe(api.submits[0]!.idempotencyKey);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }

  // 429 `spend_cap_reached`: the wait the server asked for, and the action held back until it elapses.
  {
    const api = await openActionPage(page, lang, { detail: approvedDetail(), clock: true });
    api.replySubmit({ kind: 'problem', status: 429, code: 'spend_cap_reached', retryAfter: 3 });
    await requestRender(page, makeButton(page), lang);
    await expect(makeError(page)).toContainText(errors.spend_cap_reached);
    await expect(page.locator('nts-retry-countdown')).toBeVisible();
    await expect(makeButton(page)).toBeDisabled();
    await page.clock.fastForward(3_000);
    await expect(page.locator('nts-retry-countdown'), 'the wait is over, so the countdown is gone').toHaveCount(0);
    await expect(makeButton(page), 'the action is released after the wait').toBeEnabled();
    await expect(page.locator('body')).not.toContainText(SERVER_DETAIL);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
}

/** Acceptance 8 (stubbed): one DELETE, no headers, the busy state, and the terminal state from the read. */
async function cancelFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = CANCEL_COPY[lang];
  const api = await openActionPage(page, lang, { detail: stateDetail('queued') });
  await expect(timeline(page)).toBeVisible();
  const cancel = cancelButton(page);
  await expect(cancel).toBeVisible();
  await expect(cancel).toBeEnabled();
  await expect(cancel).toHaveText(copy.action);

  api.holdCancel();
  await cancel.click();
  await expect(dialog(page)).toBeVisible();
  await expect(dialog(page)).toContainText(copy.title);
  await expect(dialog(page)).toContainText(copy.body);
  await dialogButton(page, copy.confirm).click();
  await expect(dialog(page)).toBeHidden();

  // Exactly one DELETE, without `If-Match` and without `Idempotency-Key` (the route is server-idempotent).
  await expect.poll(() => api.cancels.length).toBe(1);
  expect(api.cancels[0]!.method).toBe('DELETE');
  expect(api.cancels[0]!.path).toBe(RENDER_JOB_PATH);
  expect(api.cancels[0]!.ifMatch).toBeNull();
  expect(api.cancels[0]!.idempotencyKey).toBeNull();

  // The in-flight state: the busy label, disabled + `aria-busy`, announced by the timeline's polite live region.
  await expect(cancel).toBeDisabled();
  await expect(cancel).toHaveAttribute('aria-busy', 'true');
  await expect(cancel).toHaveText(copy.busy);
  await expect(timeline(page).locator('[aria-live="polite"]')).toContainText(copy.busy);
  await expect(statusChip(page), 'no status is painted optimistically').toContainText(CORE_ACTION[lang].status.rendering);
  await expect(timeline(page)).not.toContainText(CORE_ACTION[lang].status.cancelled);
  api.releaseCancel();

  // The terminal state is the server's: the next read reports the episode back in the approved view.
  api.serve(approvedDetail());
  api.pushEpisodeUpdated();
  await expect(makeButton(page)).toBeVisible();
  await expect(timeline(page)).toHaveCount(0);
  await expect(statusChip(page)).toContainText(CORE_ACTION[lang].status.story_approved);
  await expectA11yClean(page);
}

/** Acceptance 8 (stubbed): the affordance follows the job's status, not the server's `nextActions` alone. */
async function cancelAffordanceFlow(page: Page, lang: E2eLang): Promise<void> {
  for (const status of CLOSED_STATES) {
    // `nextActions` still carries `cancel` on purpose: only the job's own status may hide the button.
    await openActionPage(page, lang, { detail: jobDetail(status, ['cancel']) });
    await expect(cancelButton(page), `no cancel affordance for a ${status} job`).toHaveCount(0);
    if (status !== 'failed') await expect(timeline(page)).toBeVisible();
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
  for (const status of ['queued', 'running'] as const) {
    await openActionPage(page, lang, { detail: jobDetail(status, ['cancel']), theme: 'dark' });
    await expect(cancelButton(page), `the cancel affordance is offered for a ${status} job`).toBeVisible();
    await expect(cancelButton(page)).toBeEnabled();
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
}

/** Acceptance 9 (stubbed): each refusal refetches once, and the honest server state wins over an error banner. */
async function cancelRefusalFlow(page: Page, lang: E2eLang): Promise<void> {
  const errors = ACTION_ERRORS[lang];
  // The cancel error is associated with the job it refused: it stays while the refetched detail still shows that same
  // cancellable job, and is dropped once the job is gone or reports a status the server would refuse a cancel in.
  const refusals: readonly {
    readonly code: keyof ActionErrorCopy;
    readonly status: number;
    readonly next: EpisodeDetailDto;
    readonly state: 'banner' | 'ingesting' | 'approved';
  }[] = [
    // Another tab's cancel raced, and the read still returns this job cancellable: the mapped copy stays against it.
    { code: 'concurrency_conflict', status: 409, next: jobDetail('running', ['cancel']), state: 'banner' },
    // The render already ingested: the same job comes back in a status the server refuses, so the banner is dropped
    // and the timeline shows that state — there is no cancel affordance left for the error to sit beside.
    { code: 'episode_invalid_state', status: 409, next: jobDetail('ingesting', ['cancel']), state: 'ingesting' },
    // Another tab cancelled first: the job is gone, so the page returns to the approved view without a banner.
    { code: 'concurrency_conflict', status: 409, next: approvedDetail(), state: 'approved' },
    // Not this user's job (or already gone): the same honest state, and no copy left over a job that no longer exists.
    { code: 'not_found', status: 404, next: approvedDetail(), state: 'approved' },
  ];
  for (const refusal of refusals) {
    const api = await openActionPage(page, lang, { detail: stateDetail('queued') });
    const settled = await settleActionReads(page, api);
    api.replyCancel({ kind: 'problem', status: refusal.status, code: refusal.code });
    api.serve(refusal.next);
    await cancelButton(page).click();
    await expect(dialog(page)).toBeVisible();
    await dialogButton(page, CANCEL_COPY[lang].confirm).click();
    await expect(dialog(page)).toBeHidden();
    await expect.poll(() => api.cancels.length, { message: `${refusal.code} is one DELETE` }).toBe(1);
    await expect.poll(() => api.detailGets(), { message: `${refusal.code} refetches the episode once` }).toBe(settled + 1);
    await expectNoFurtherReads(page, api, settled + 1);
    if (refusal.state === 'banner') {
      await expect(cancelError(page)).toContainText(errors[refusal.code]);
      await expect(cancelButton(page), 'the job the server refused is still cancellable').toBeEnabled();
    } else if (refusal.state === 'ingesting') {
      await expect(timeline(page)).toBeVisible();
      await expect(activeStage(page), 'the timeline shows the job the server reported').toContainText(RENDER_COPY[lang].stage.ingesting);
      await expect(cancelButton(page), 'no cancel affordance for a job the server reports as ingesting').toHaveCount(0);
      await expect(page.locator('body')).not.toContainText(errors[refusal.code]);
    } else {
      await expect(makeButton(page), 'the episode is back in the approved view').toBeVisible();
      await expect(page.locator('body')).not.toContainText(errors[refusal.code]);
    }
    await expect(page.locator('body')).not.toContainText(SERVER_DETAIL);
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
}

/**
 * Acceptance 10 (stubbed): axe clean on both dialogs, the blocked button and the cancelling state, in both themes.
 * Every dismissal — the dialog's own Cancel and Escape, which share the one `close(false)` path — returns the focus to
 * the trigger it was opened from, including after an accepted confirm, where the trigger is only `aria-disabled`.
 */
async function axeActionFlow(page: Page, lang: E2eLang): Promise<void> {
  const cancels = CANCEL_COPY[lang];
  for (const theme of ['light', 'dark'] as const) {
    // (1) The make confirm, and the blocked (disabled) button state it leaves behind.
    const api = await openActionPage(page, lang, { detail: approvedDetail(), theme });
    // The page is settled before the dialog opens: the header and the timeline sit in one branch of the page, and the
    // boot reads that recreate it would detach this trigger as well (measured on the cancel side below).
    await settleActionReads(page, api);
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    const make = makeButton(page);
    await make.click();
    await waitForOverlayAnimationEnd(page);
    expect(await page.evaluate(() => document.activeElement?.closest('[role="dialog"]') !== null), 'the dialog traps focus').toBe(true);
    await expectA11yClean(page);
    await dialogButton(page, CORE_ACTION[lang].dialogCancel).click();
    await expect(dialog(page)).toBeHidden();
    await expect(make, 'focus is restored to the trigger').toBeFocused();
    await expectA11yClean(page);

    // Escape is the dialog's own `close(false)` path as well, and it restores the focus to the very same trigger.
    await make.click();
    await waitForOverlayAnimationEnd(page);
    await page.keyboard.press('Escape');
    await expect(dialog(page)).toBeHidden();
    await expect(make, 'Escape restores the focus to the trigger').toBeFocused();
    await expectA11yClean(page);

    api.replySubmit({ kind: 'problem', status: 409, code: 'keywords_flagged', errors: [FLAG_ROW] });
    await requestRender(page, make, lang);
    await expect(make).toBeDisabled();
    await expect(makeError(page)).toContainText(ACTION_ERRORS[lang].keywords_flagged);
    // The accepted press only marks the trigger `aria-disabled` (never natively `disabled`), so it stays focusable and
    // MatDialog's `restoreFocus` hands the focus back once the exit animation ends.
    await expect(make, 'an accepted make confirm restores the focus to the trigger').toBeFocused();
    await expect(make).toHaveAttribute('aria-disabled', 'true');
    await expectA11yClean(page);
    await page.unrouteAll({ behavior: 'ignoreErrors' });

    // (2a) The cancel confirm dialog: trapped focus, axe clean, and — dismissed from the dialog's own Cancel, with
    // nothing sent — the focus returns to the trigger it was opened from.
    const queued = await openActionPage(page, lang, { detail: stateDetail('queued'), theme });
    // A read still landing after the dialog opened re-creates the whole render branch (`@if (renderView())`), which
    // detaches the cancel trigger the dialog captured: settle the page first so the same node is still there on close.
    await settleActionReads(page, queued);
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    const cancel = cancelButton(page);
    await cancel.click();
    await waitForOverlayAnimationEnd(page);
    expect(await page.evaluate(() => document.activeElement?.closest('[role="dialog"]') !== null), 'the dialog traps focus').toBe(true);
    await expectA11yClean(page);
    await dialogButton(page, CORE_ACTION[lang].dialogCancel).click();
    await expect(dialog(page)).toBeHidden();
    await expect(cancel, 'focus is restored to the trigger').toBeFocused();
    await expectA11yClean(page);

    // Escape is the same `close(false)` path, and nothing is sent either way.
    await cancel.click();
    await waitForOverlayAnimationEnd(page);
    await page.keyboard.press('Escape');
    await expect(dialog(page)).toBeHidden();
    await expect(cancel, 'Escape restores the focus to the trigger').toBeFocused();
    await expectA11yClean(page);

    // (2b) The cancelling state (the DELETE held in flight) is axe clean, and the accepted command keeps the trigger
    // in the document — `aria-disabled` + `aria-busy` only — so MatDialog's `restoreFocus` still has a focusable target
    // when the exit animation ends.
    queued.holdCancel();
    await cancel.click();
    await waitForOverlayAnimationEnd(page);
    await expectA11yClean(page);
    await dialogButton(page, cancels.confirm).click();
    await expect(dialog(page)).toBeHidden();
    await expect(cancel).toBeDisabled();
    await expect(cancel).toHaveAttribute('aria-disabled', 'true');
    await expect(cancel).toHaveAttribute('aria-busy', 'true');
    await expect(cancel).toHaveText(cancels.busy);
    await expect(cancel, 'an accepted cancel restores the focus to the trigger').toBeFocused();
    await expectA11yClean(page);
    queued.releaseCancel();
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  }
}

/** Acceptance 10 (stubbed): 360 px with no horizontal scroll, and the same command under `prefers-reduced-motion`. */
async function smallViewportActionFlow(page: Page, lang: E2eLang): Promise<void> {
  await page.setViewportSize({ width: 360, height: 740 });
  const api = await openActionPage(page, lang, { detail: approvedDetail() });
  await expectNoHorizontalScroll(page);
  const make = makeButton(page);
  await make.click();
  await expect(dialog(page)).toBeVisible();
  await expectNoHorizontalScroll(page);
  await dialogButton(page, CORE_ACTION[lang].dialogCancel).click();
  await expect(dialog(page)).toBeHidden();
  await expect(make).toBeFocused();
  await expectNoHorizontalScroll(page);

  api.replySubmit({ kind: 'problem', status: 429, code: 'spend_cap_reached', retryAfter: 2 }, { kind: 'accepted' });
  await requestRender(page, make, lang);
  await expect(makeError(page)).toContainText(ACTION_ERRORS[lang].spend_cap_reached);
  await expectNoHorizontalScroll(page);
  await page.unrouteAll({ behavior: 'ignoreErrors' });

  await page.emulateMedia({ reducedMotion: 'reduce' });
  const motionApi = await openActionPage(page, lang, { detail: approvedDetail() });
  await requestRender(page, makeButton(page), lang);
  await expect.poll(() => motionApi.submits.length).toBe(1);
  expect(motionApi.submits[0]!.path).toBe(RENDER_POST_PATH);
  await expect(makeError(page), 'the command works the same under reduced motion').toHaveCount(0);
}

// ---------------------------------------------------------------------------------------------------------------
// Spec
// ---------------------------------------------------------------------------------------------------------------

const LANGS = [{ lang: 'vi' as const, locale: 'vi-VN' }, { lang: 'en' as const, locale: 'en-US' }] as const;

for (const { lang, locale } of LANGS) {
  test.describe(`render timeline (${lang}, stubbed)`, () => {
    test.use({ locale, timezoneId: 'UTC' });

    test('Queued_ThreeStagesInOrder_ZeroPercentElapsedEmailNotice_ThenWaitingNoticeAfter30s_NoReadPerTick', async ({ page }) => {
      await queuedFlow(page, lang);
    });

    test('RenderProgress_MovesTheBar_ShowsTheSubStep_UnknownStageAndOverRangePercentNeverRaw_NoRefetchPerFrame', async ({ page }) => {
      await runningFlow(page, lang);
    });

    test('Ingesting_TerminalStatusArrivesThroughEpisodeUpdated_WithExactlyOneRefetch', async ({ page }) => {
      await ingestingTerminalFlow(page, lang);
    });

    test('FailedQualityFailed_ShowsErrorTitle_HidesTheBar_OffersTheLiveRetry_AndNoServerTextOrParams', async ({ page }) => {
      await failedQualityFlow(page, lang);
    });

    test('FailedModerated_ListsBothScenesWithReasons_GuidanceAndSegmentDeepLink_HintWithoutEditAction', async ({ page }) => {
      await failedModeratedFlow(page, lang);
    });

    test('AxeClean_LightAndDark_OnEveryState_AndNoCspOrTrustedTypesViolation', async ({ page, cspLog }) => {
      test.setTimeout(120_000);
      await axeFlow(page, lang);
      expect(cspLog, `CSP / Trusted Types violation(s) recorded:\n${describeCsp(cspLog)}`).toEqual([]);
    });

    test('AxeClean_LightAndDark_OnTheWaitingNotice', async ({ page }) => {
      test.setTimeout(60_000);
      await axeWaitingFlow(page, lang);
    });

    test('Narrow360_EveryStateHasNoHorizontalScroll', async ({ page }) => {
      test.setTimeout(60_000);
      await narrow360Flow(page, lang);
    });

    test('ReducedMotion_DisablesTheProgressBarTransition', async ({ page }) => {
      await reducedMotionFlow(page, lang);
    });

    test('MakeVideo_OneConfirmThenOnePost_IfMatchAndUuidKey_AndNoStatusWrittenBeforeTheRead', async ({ page }) => {
      test.setTimeout(60_000);
      await makeVideoHappyPath(page, lang);
    });

    test('MakeVideo_AbsentWithoutRenderOrRetry_RetryLabel_TheFailurePanelRetryRunsTheSameCommand', async ({ page }) => {
      test.setTimeout(60_000);
      await makeVideoAffordanceFlow(page, lang);
    });

    test('MakeVideo_SameKeyAfterALostResponse_NewKeyAfterATerminalAnswer_AcrossCancelAndMakeAgain', async ({ page }) => {
      test.setTimeout(90_000);
      await makeVideoIdempotencyFlow(page, lang);
    });

    test('MakeVideo_KeywordsFlagged_BlocksTheButtonAndFlagsTheSegmentInTheEditor', async ({ page }) => {
      test.setTimeout(60_000);
      await makeVideoKeywordsFlow(page, lang);
    });

    test('MakeVideo_InvalidStateConflictAndSpendCap_RefetchOnceWithTheMappedCopyAndNoServerDetail', async ({ page }) => {
      test.setTimeout(90_000);
      await makeVideoRefusalFlow(page, lang);
    });

    test('CancelRender_OneDeleteWithoutHeaders_BusyUntilTheReadReportsTheTerminalState', async ({ page }) => {
      test.setTimeout(60_000);
      await cancelFlow(page, lang);
    });

    test('CancelRender_AffordanceFollowsTheJobStatus_NotTheServersNextActions', async ({ page }) => {
      test.setTimeout(90_000);
      await cancelAffordanceFlow(page, lang);
    });

    test('CancelRender_InvalidStateConflictAndNotFound_RefetchOnceAndShowTheHonestState', async ({ page }) => {
      test.setTimeout(90_000);
      await cancelRefusalFlow(page, lang);
    });

    test('AxeClean_MakeAndCancelDialogs_BlockedButtonAndCancellingState_BothThemes_AndNoCspViolation', async ({ page, cspLog }) => {
      test.setTimeout(180_000);
      await axeActionFlow(page, lang);
      expect(cspLog, `CSP / Trusted Types violation(s) recorded:\n${describeCsp(cspLog)}`).toEqual([]);
    });

    test('Narrow360_NoHorizontalScrollOnTheDialogAndTheRefusal_AndReducedMotionKeepsTheCommand', async ({ page }) => {
      test.setTimeout(90_000);
      await smallViewportActionFlow(page, lang);
    });
  });
}
