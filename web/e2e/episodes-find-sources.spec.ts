import type { Page, Request } from '@playwright/test';
import { expect, test } from './fixtures/csp.fixture';
import { expectA11yClean } from './fixtures/axe';
import { EPISODE_ID, episodeDetail, stubEpisodes, type EpisodeStubs } from './fixtures/episodes-stubs';
import { RESEARCH_PATH, researchSource, stubResearch, type ResearchServer } from './fixtures/research-stubs';
import { fulfilJson, meDto, problem, type E2eLang } from './fixtures/series-stubs';
import type { EpisodeDetailDto } from '../src/app/shared/api/models/episode-detail-dto';
import type { GenerationRunStatus } from '../src/app/shared/api/models/generation-run-status';
import type { SourceCandidateDto } from '../src/app/shared/api/models/source-candidate-dto';
import type { SourceListItemDto } from '../src/app/shared/api/models/source-list-item-dto';
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import episodesVi from '../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import errorsEn from '../src/assets/i18n/errors/en.json' with { type: 'json' };
import errorsVi from '../src/assets/i18n/errors/vi.json' with { type: 'json' };

// 10.5.8-T1: backend-free proof of the find-sources block (flag `source_finder`) on the research page. The run state
// is the episode's own `generation` (02.7 plan §5), so every step is driven through the detail stub and one realtime
// invalidation — the page is never polled and no test asserts incidental copy or the idempotency key's format.

const copy = { en: episodesEn, vi: episodesVi };
const errors = { en: errorsEn, vi: errorsVi };
const EPISODE_API = '/api/v1/episodes/' + EPISODE_ID;
const FIND_PATH = EPISODE_API + '/sources/find';
const CANDIDATES_PATH = EPISODE_API + '/sources/candidates';
/** Candidate/site host: stubbed so the "Open" tab never leaves for the network. */
const CANDIDATE_HOST = 'example.test';
const STARTED_AT = '2026-10-15T12:00:00Z';
const FEATURE = 'source_finder';

interface FinderWorld {
  readonly stub: EpisodeStubs;
  readonly server: ResearchServer;
  /** Every `GET /api/v1/me` the page made, so the 404 `feature_disabled` refetch is observable. */
  readonly meGets: Request[];
  readonly findPosts: Request[];
  readonly candidateGets: Request[];
  readonly rowPosts: Request[];
  detail: EpisodeDetailDto;
  candidates: SourceCandidateDto[];
  /** Non-null replaces the 202 answer of `POST …/sources/find` with that problem. */
  findReply: { readonly status: number; readonly code: string } | null;
}

function idleDetail(): EpisodeDetailDto {
  return episodeDetail('queued', { status: 'draft', generation: null, nextActions: ['generate_story'] });
}

function runDetail(status: GenerationRunStatus, detail: EpisodeDetailDto): EpisodeDetailDto {
  return {
    ...detail,
    generation: { kind: 'find_sources', status, stage: status === 'succeeded' ? 'done' : 'reviewing', errorCode: null, startedAt: STARTED_AT },
  };
}

function candidateRow(index: number, overrides: Partial<SourceCandidateDto> = {}): SourceCandidateDto {
  return {
    id: 'candidate-' + index,
    url: 'https://' + CANDIDATE_HOST + '/story-' + index,
    title: 'Search result ' + index,
    siteName: CANDIDATE_HOST,
    rank: index,
    status: 'proposed',
    sourceId: null,
    createdAt: STARTED_AT,
    ...overrides,
  };
}

/** Registers every stub the page needs. Navigation is the caller's, so a test can listen for requests first. */
async function stubFinder(
  page: Page,
  lang: E2eLang,
  options: { readonly features?: readonly string[] | 'absent'; readonly sources?: readonly SourceListItemDto[] } = {},
): Promise<FinderWorld> {
  const stub = await stubEpisodes(page, { lang, topic: 'true_crime' });
  const meGets: Request[] = [];
  const features = options.features ?? [FEATURE];
  // Registered after `stubEpisodes`, so this profile answer (feature list included) wins over the shared one.
  await page.route(
    (url) => url.pathname === '/api/v1/me',
    async (route) => {
      if (route.request().method() !== 'GET') return route.fallback();
      meGets.push(route.request());
      const body: Record<string, unknown> = { ...meDto(lang) };
      if (features === 'absent') delete body['features'];
      else body['features'] = features;
      return fulfilJson(route, 200, body);
    },
  );
  const server = await stubResearch(page, {
    sources: [...(options.sources ?? [researchSource(1), researchSource(2)])],
    sheet: null,
    generating: false,
    requests: [],
  });
  const world: FinderWorld = { stub, server, meGets, findPosts: [], candidateGets: [], rowPosts: [], detail: idleDetail(), candidates: [], findReply: null };
  // The episode detail is the run state's only source (a reloaded page reads it again), so it is stubbed here.
  await page.route((url) => url.pathname === EPISODE_API, (route) => fulfilJson(route, 200, world.detail, { ETag: world.detail.etag }));
  await page.route((url) => url.pathname === CANDIDATES_PATH, (route) => {
    world.candidateGets.push(route.request());
    return fulfilJson(route, 200, { items: world.candidates });
  });
  await page.route((url) => url.pathname === FIND_PATH, (route) => {
    world.findPosts.push(route.request());
    if (world.findReply !== null) {
      return fulfilJson(route, world.findReply.status, problem(world.findReply.status, world.findReply.code), { 'Content-Type': 'application/problem+json' });
    }
    // 202 accepted: from here the run is the episode's own generation, which the detail stub answers.
    world.detail = runDetail('running', world.detail);
    return fulfilJson(route, 202, { generationRunId: 'find-run-1', status: 'queued' });
  });
  await page.route((url) => /^\/api\/v1\/episodes\/[^/]+\/sources\/candidates\/[^/]+\/(add|dismiss)$/.test(url.pathname), (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname.split('/');
    const candidateId = path[path.length - 2] ?? '';
    world.rowPosts.push(request);
    if (path[path.length - 1] === 'dismiss') {
      world.candidates = world.candidates.map((candidate) => (candidate.id === candidateId ? { ...candidate, status: 'dismissed' } : candidate));
      return route.fulfill({ status: 204 });
    }
    const candidate = world.candidates.find((row) => row.id === candidateId);
    const source = { ...researchSource(world.server.sources.length + 1), url: candidate?.url ?? null, title: candidate?.title ?? candidate?.url ?? null };
    // 201: the suggestion is a source of the episode now, so the sources list above must show it after its reload.
    world.candidates = world.candidates.map((row) => (row.id === candidateId ? { ...row, status: 'added', sourceId: source.id } : row));
    world.server.sources.push(source);
    return fulfilJson(route, 201, { id: source.id });
  });
  // Context-wide: the "Open" link's new tab is a page of its own and must not reach for the real network.
  await page.context().route((url) => url.hostname === CANDIDATE_HOST, (route) =>
    route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><html lang="en"><head><title>Stub</title></head><body>Stub</body></html>' }),
  );
  return world;
}

async function gotoResearch(page: Page, lang: E2eLang): Promise<void> {
  await page.goto(RESEARCH_PATH);
  await expect(page.getByRole('heading', { name: copy[lang].research.title, exact: true })).toBeVisible();
}

/** One realtime invalidation: the page re-reads the detail (run state) and the candidates, without polling. */
async function push(world: FinderWorld, status: GenerationRunStatus): Promise<void> {
  world.detail = runDetail(status, world.detail);
  await expect.poll(world.stub.connected).toBe(1);
  world.stub.push({ episodeId: EPISODE_ID });
}

const runButton = (page: Page, lang: E2eLang) => page.locator('nts-source-finder-panel').getByRole('button', { name: copy[lang].sources.find.button, exact: true });
const row = (page: Page, index: number) => page.locator('nts-source-finder-panel .candidate-row').nth(index);
const addButton = (page: Page, lang: E2eLang, index: number) =>
  row(page, index).getByRole('button', { name: copy[lang].sources.find.addFromSite.replace('{{site}}', CANDIDATE_HOST), exact: true });
const dismissButton = (page: Page, lang: E2eLang, index: number) =>
  row(page, index).getByRole('button', { name: copy[lang].sources.find.dismissFromSite.replace('{{site}}', CANDIDATE_HOST), exact: true });
const openLink = (page: Page, lang: E2eLang, index: number) =>
  row(page, index).getByRole('link', { name: copy[lang].sources.find.openNewTab.replace('{{site}}', CANDIDATE_HOST), exact: true });

/** D14: a spend-cap banner carries no amount, price, credit or balance figure. */
function expectNoAmounts(text: string): void {
  expect(text, 'a spend-cap banner must carry no digit').not.toMatch(/\d/);
  expect(text.toLowerCase(), 'a spend-cap banner must carry no credit/balance/price wording').not.toMatch(/credit|balance|price|số dư|tín dụng|giá/);
}

for (const lang of ['vi', 'en'] as const) {
  const c = copy[lang];
  test.describe('find sources ' + lang, () => {
    test.use({ locale: lang === 'vi' ? 'vi-VN' : 'en-US', timezoneId: 'UTC' });

    test('flag on: run, suggestions only, sources unchanged until Add, then Add and Dismiss', async ({ page }) => {
      const world = await stubFinder(page, lang);
      await gotoResearch(page, lang);
      const panel = page.locator('nts-source-finder-panel');
      await expect(panel).toBeVisible();
      await expect(panel.getByText(c.sources.find.note, { exact: true })).toBeVisible();
      await expect(panel.locator('input[type=checkbox], input[type=radio]')).toHaveCount(0);
      const sources = page.locator('nts-source-row');
      await expect(sources).toHaveCount(2);
      await expectA11yClean(page);

      // An optional focus field: a run without one sends no query, and the intent key is always on the header.
      await runButton(page, lang).click();
      await expect.poll(() => world.findPosts.length).toBe(1);
      const post = world.findPosts[0]!;
      expect(post.method()).toBe('POST');
      expect(post.headers()['idempotency-key']).toBeTruthy();
      expect(post.postDataJSON()).toEqual({ query: null });

      await push(world, 'running');
      await expect(panel.locator('.running')).toContainText(c.sources.find.running);
      await expect(page.locator('.source-section [role=status]')).toContainText(c.sources.find.running);
      await expect(runButton(page, lang)).toBeDisabled();

      const hostile = '<img src=x onerror=alert(1)>';
      world.candidates = [candidateRow(1), candidateRow(2, { title: null }), candidateRow(3, { title: hostile })];
      await push(world, 'succeeded');
      await expect(panel.locator('.candidate-row')).toHaveCount(3);
      await expect(panel.locator('.candidate-title')).toHaveText(['Search result 1', 'https://' + CANDIDATE_HOST + '/story-2', hostile]);
      // Untrusted text stays inert and no suggestion is pre-selected.
      await expect(panel.locator('img')).toHaveCount(0);
      await expect(panel.locator('input[type=checkbox], input[type=radio]')).toHaveCount(0);
      for (let index = 0; index < 3; index += 1) {
        const link = openLink(page, lang, index);
        await expect(link).toHaveAttribute('target', '_blank');
        await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
        await expect(addButton(page, lang, index)).toBeVisible();
        await expect(dismissButton(page, lang, index)).toBeVisible();
      }
      // Nothing is ever added on its own: the sources list is untouched by a list of suggestions.
      await expect(sources).toHaveCount(2);
      await expectA11yClean(page);

      await addButton(page, lang, 0).click();
      await expect.poll(() => world.rowPosts.filter((request) => request.url().endsWith('/add')).length).toBe(1);
      await expect(sources).toHaveCount(3);
      await expect(sources.filter({ hasText: 'Search result 1' })).toHaveCount(1);
      await expect(row(page, 0).locator('.candidate-status')).toContainText(c.sources.find.added);

      await dismissButton(page, lang, 1).click();
      await expect.poll(() => world.rowPosts.filter((request) => request.url().endsWith('/dismiss')).length).toBe(1);
      await expect(row(page, 1).locator('.candidate-status')).toContainText(c.sources.find.dismissed);
      // A dismissal never touches the sources list.
      await expect(sources).toHaveCount(3);
      await expectA11yClean(page);
    });

    test('flag on: the focus field is sent as the query, capped at 300 chars', async ({ page }) => {
      const world = await stubFinder(page, lang);
      await gotoResearch(page, lang);
      const panel = page.locator('nts-source-finder-panel');
      const query = 'x'.repeat(300);
      const input = panel.getByRole('textbox', { name: c.sources.find.queryLabel, exact: true });
      await input.fill(query);
      await input.press('End');
      await page.keyboard.type('y');
      await expect(input).toHaveValue(query);
      await runButton(page, lang).click();
      await expect.poll(() => world.findPosts.length).toBe(1);
      expect(world.findPosts[0]!.postDataJSON()).toEqual({ query });
      // 202 accepted: the run is the episode's own generation, which the page reads on the invalidation.
      await push(world, 'running');
      await expect(panel.locator('.running')).toContainText(c.sources.find.running);
    });

    test('flag on: a reload during a run still shows the searching state', async ({ page }) => {
      const world = await stubFinder(page, lang);
      await gotoResearch(page, lang);
      const panel = page.locator('nts-source-finder-panel');
      await runButton(page, lang).click();
      await push(world, 'running');
      await expect(panel.locator('.running')).toContainText(c.sources.find.running);

      await page.reload();
      await expect(page.getByRole('heading', { name: c.research.title, exact: true })).toBeVisible();
      await expect(panel).toBeVisible();
      await expect(panel.locator('.running')).toContainText(c.sources.find.running);
      await expect(page.locator('.source-section [role=status]')).toContainText(c.sources.find.running);
      await expect(runButton(page, lang)).toBeDisabled();
      await expectA11yClean(page);
    });

    test('flag on: keyboard-only run, open, add and dismiss', async ({ page }) => {
      const world = await stubFinder(page, lang);
      await gotoResearch(page, lang);
      const panel = page.locator('nts-source-finder-panel');
      await runButton(page, lang).press('Enter');
      await expect.poll(() => world.findPosts.length).toBe(1);
      await push(world, 'running');
      await expect(panel.locator('.running')).toContainText(c.sources.find.running);

      const candidate = candidateRow(1);
      world.candidates = [candidate, candidateRow(2)];
      await push(world, 'succeeded');
      await expect(panel.locator('.candidate-row')).toHaveCount(2);

      const link = openLink(page, lang, 0);
      await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
      const popup = page.waitForEvent('popup');
      await link.press('Enter');
      const opened = await popup;
      expect(opened.url()).toBe(candidate.url);
      await opened.close();

      const add = addButton(page, lang, 0);
      await expect(add).toHaveAttribute('aria-label', c.sources.find.addFromSite.replace('{{site}}', CANDIDATE_HOST));
      await add.press('Enter');
      await expect.poll(() => world.rowPosts.filter((request) => request.url().endsWith('/add')).length).toBe(1);
      await expect(row(page, 0).locator('.candidate-status')).toContainText(c.sources.find.added);

      const dismiss = dismissButton(page, lang, 1);
      await expect(dismiss).toHaveAttribute('aria-label', c.sources.find.dismissFromSite.replace('{{site}}', CANDIDATE_HOST));
      await dismiss.press('Enter');
      await expect.poll(() => world.rowPosts.filter((request) => request.url().endsWith('/dismiss')).length).toBe(1);
      await expect(row(page, 1).locator('.candidate-status')).toContainText(c.sources.find.dismissed);
      await expectA11yClean(page);
    });

    test('flag on: the idle block is axe clean', async ({ page }) => {
      await stubFinder(page, lang);
      await gotoResearch(page, lang);
      const panel = page.locator('nts-source-finder-panel');
      await expect(panel).toBeVisible();
      await expect(runButton(page, lang)).toBeEnabled();
      await expectA11yClean(page);
    });

    test('no source_finder in features: no block and no finder request', async ({ page }) => {
      const finder: string[] = [];
      page.on('request', (request) => {
        const path = new URL(request.url()).pathname;
        if (path === FIND_PATH || path === CANDIDATES_PATH) finder.push(path);
      });
      await stubFinder(page, lang, { features: ['youtube_publish'] });
      await gotoResearch(page, lang);
      await expect(page.locator('nts-source-row')).toHaveCount(2);
      await expect(page.locator('nts-source-finder-panel')).toHaveCount(0);
      await expect(page.getByText(c.sources.find.button, { exact: true })).toHaveCount(0);
      await expectA11yClean(page);
      expect(finder).toEqual([]);
    });

    test('features absent from the profile: the block fails closed with no finder request', async ({ page }) => {
      const finder: string[] = [];
      page.on('request', (request) => {
        const path = new URL(request.url()).pathname;
        if (path === FIND_PATH || path === CANDIDATES_PATH) finder.push(path);
      });
      await stubFinder(page, lang, { features: 'absent' });
      await gotoResearch(page, lang);
      await expect(page.locator('nts-source-row')).toHaveCount(2);
      await expect(page.locator('nts-source-finder-panel')).toHaveCount(0);
      await expectA11yClean(page);
      expect(finder).toEqual([]);
    });

    test('404 feature_disabled hides the block, re-reads the profile and shows no error', async ({ page }) => {
      const world = await stubFinder(page, lang);
      await gotoResearch(page, lang);
      const panel = page.locator('nts-source-finder-panel');
      await expect(panel).toBeVisible();
      const meBefore = world.meGets.length;
      world.findReply = { status: 404, code: 'feature_disabled' };
      await runButton(page, lang).click();
      await expect.poll(() => world.findPosts.length).toBe(1);
      await expect(panel).toHaveCount(0);
      await expect.poll(() => world.meGets.length).toBeGreaterThan(meBefore);
      await expect(page.locator('[role=alert]')).toHaveCount(0);
      await expect(page.locator('nts-source-row')).toHaveCount(2);
      await expectA11yClean(page);
    });

    test('429 spend_cap_reached shows a banner without any amount', async ({ page }) => {
      const world = await stubFinder(page, lang);
      await gotoResearch(page, lang);
      const panel = page.locator('nts-source-finder-panel');
      world.findReply = { status: 429, code: 'spend_cap_reached' };
      await runButton(page, lang).click();
      const alert = panel.locator('.error[role=alert]');
      await expect(alert).toContainText(errors[lang].spend_cap_reached);
      expectNoAmounts(await alert.innerText());
      await expect(runButton(page, lang)).toBeEnabled();
      await expectA11yClean(page);
    });
  });
}
