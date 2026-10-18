import type { Locator, Page, WebSocketRoute } from '@playwright/test';
import type { EpisodeDetailDto } from '../src/app/shared/api/models/episode-detail-dto';
import type { RenderJobSummaryDto } from '../src/app/shared/api/models/render-job-summary-dto';
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import episodesVi from '../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import errorsEn from '../src/assets/i18n/errors/en.json' with { type: 'json' };
import errorsVi from '../src/assets/i18n/errors/vi.json' with { type: 'json' };
import { expectA11yClean } from './fixtures/axe';
import { describeCsp, expect, test } from './fixtures/csp.fixture';
import { episodeDetail, EPISODE_ID, SERIES_ID } from './fixtures/episodes-stubs';
import { expectNoHorizontalScroll, fulfilJson, seriesDetail, stubSeriesApi, stubSession, type E2eLang } from './fixtures/series-stubs';

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

  let current = stateDetail(state);
  let gets = 0;
  let connections = 0;
  let socket: WebSocketRoute | null = null;
  let handshaken = false;

  // `stubSession` answers 503 here so no spec connects by accident; this spec drives the real hub path instead.
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
  await page.route(`**/api/v1/episodes/${EPISODE_ID}`, async (route) => {
    gets += 1;
    await fulfilJson(route, 200, current, { ETag: current.etag });
  });

  await page.goto(RENDER_PATH);
  await expect(page.locator('html')).toHaveAttribute('lang', lang);

  const push = (target: string, payload: unknown): void => {
    if (socket === null || !handshaken) throw new Error('SignalR handshake has not completed');
    socket.send(`${JSON.stringify({ type: 1, target, arguments: [payload] })}${RECORD_SEPARATOR}`);
  };
  return {
    detailGets: () => gets,
    connected: () => connections,
    serve: (next) => { current = next; },
    pushProgress: (progress) => push('RenderProgress', { ids: { episodeId: EPISODE_ID, renderJobId: RENDER_JOB_ID }, data: progress }),
    pushEpisodeUpdated: () => push('EpisodeUpdated', { ids: { episodeId: EPISODE_ID, seriesId: SERIES_ID } }),
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
 * Acceptance 9 (stubbed): `render_quality_failed` renders the shared `errors.<code>` title, hides the bar, and offers no
 * enabled retry here — the page passes `nextActions` without `retry_render` (that affordance is 10.6.1-T2's).
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
  const retryCount = await retry.count();
  expect(retryCount, 'at most one retry affordance').toBeLessThanOrEqual(1);
  if (retryCount > 0) {
    expect(await retry.isEnabled(), 'retry is not offered by this task: the page passes nextActions without retry_render').toBe(false);
  }

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

    test('FailedQualityFailed_ShowsErrorTitle_HidesTheBar_OffersNoEnabledRetry_AndNoServerTextOrParams', async ({ page }) => {
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
  });
}
