import type { Page, Request, Route } from '@playwright/test';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { SERIES_ID, fulfilJson, problem, stubSession, type E2eLang } from './fixtures/series-stubs';
import seriesEn from '../src/assets/i18n/series/en.json' with { type: 'json' };
import seriesVi from '../src/assets/i18n/series/vi.json' with { type: 'json' };
import errorsVi from '../src/assets/i18n/errors/vi.json' with { type: 'json' };

// 10.5.7-T1 (README D15/D16, task file §Tests `series-bible.spec.ts` + Acceptance checks).
// Backend-free: the session, `GET`/`PUT /api/v1/series/{id}/bible`, the accept/dismiss endpoints and the one
// `GET /api/v1/episodes/{id}` title lookup are stubbed with `page.route`. The wire casing mirrors the generated
// client (`BibleDto` is camelCase, its `content` is the snake_case `bible.v1` document) because the mapper is the
// only camelCase <-> snake_case boundary.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

const COPY = { vi: seriesVi.bible, en: seriesEn.bible } as const;
const L = COPY.vi;
const ERR = errorsVi;

const BIBLE_PATH = `/series/${SERIES_ID}/bible`;
const BIBLE_API = `/api/v1/series/${SERIES_ID}/bible`;
const OTHER_EPISODE = '55555555-5555-4555-8555-555555555555';
const PROPOSING_EPISODE = '66666666-6666-4666-8666-666666666666';
const PROPOSING_TITLE = 'Tập đề xuất';

// ---------------------------------------------------------------------------------------------------------------
// Wire fixtures (`bible.v1`, 02.6.3 §3.1): camelCase DTO members, snake_case `content`
// ---------------------------------------------------------------------------------------------------------------

interface BibleNarratorWire {
  readonly name: string;
  readonly persona: string;
  readonly voice_notes: string;
}
interface BibleCharacterWire {
  readonly id: string | null;
  readonly name: string;
  readonly description: string;
  readonly first_episode_id: string | null;
}
interface BiblePlaceWire {
  readonly id: string | null;
  readonly name: string;
  readonly description: string;
}
interface BibleRuleWire {
  readonly id: string | null;
  readonly text: string;
}
interface BibleSummaryWire {
  readonly episode_id: string;
  readonly title: string;
  readonly summary: string;
  readonly structure?: unknown;
  readonly approved_at?: string;
}
interface BibleContentWire {
  readonly schema_version?: string;
  readonly narrator: BibleNarratorWire;
  readonly recurring_characters: readonly BibleCharacterWire[];
  readonly places: readonly BiblePlaceWire[];
  readonly rules: readonly BibleRuleWire[];
  readonly episode_summaries: readonly BibleSummaryWire[];
}
interface BibleOpWire {
  readonly kind: string;
  readonly name: string | null;
  readonly description: string | null;
  readonly text: string | null;
  readonly summary: string | null;
  readonly reason: string | null;
}
interface BibleProposalWire {
  readonly basedOnVersion: number;
  readonly proposedFromEpisodeId: string | null;
  readonly proposedAt: string;
  readonly ops: readonly BibleOpWire[];
  readonly preview: {
    readonly characters: number;
    readonly places: number;
    readonly rules: number;
    readonly summaryForEpisodeId: string | null;
  };
}
interface BibleWire {
  readonly seriesId: string;
  readonly version: number;
  readonly etag: string;
  readonly content: BibleContentWire;
  readonly proposal: BibleProposalWire | null;
}

function bibleContent(overrides: Partial<BibleContentWire> = {}): BibleContentWire {
  return {
    schema_version: 'bible.v1',
    narrator: { name: 'Người kể chính', persona: 'Giọng kể khàn, chậm.', voice_notes: 'Ngắt nghỉ sau mỗi câu.' },
    recurring_characters: [{ id: 'ch1', name: 'Bà Tư', description: 'Người giữ đền.', first_episode_id: OTHER_EPISODE }],
    places: [{ id: 'pl1', name: 'Ngôi đền', description: 'Bên bờ sông.' }],
    rules: [{ id: 'rl1', text: 'Không ai được gọi tên người chết.' }],
    episode_summaries: [
      { episode_id: OTHER_EPISODE, title: 'Tập một', summary: 'Một đêm mưa.', structure: { sub_genre: 'ghost' }, approved_at: '2026-10-10T00:00:00Z' },
    ],
    ...overrides,
  };
}

function bibleWire(overrides: Partial<BibleWire> = {}): BibleWire {
  return { seriesId: SERIES_ID, version: 1, etag: '"sb-1"', content: bibleContent(), proposal: null, ...overrides };
}

const PREVIEW = { characters: 2, places: 1, rules: 3, summaryForEpisodeId: PROPOSING_EPISODE } as const;

function proposalWire(overrides: Partial<BibleProposalWire> = {}): BibleProposalWire {
  return {
    basedOnVersion: 1,
    proposedFromEpisodeId: PROPOSING_EPISODE,
    proposedAt: '2026-10-16T00:00:00Z',
    ops: [
      { kind: 'AddCharacter', name: 'Ông Ba', description: 'Người lái đò.', text: null, summary: null, reason: 'Xuất hiện ở tập mới.' },
      { kind: 'AddPlace', name: 'Bến đò', description: 'Bến cũ.', text: null, summary: null, reason: 'Bối cảnh mới.' },
      { kind: 'AddRule', name: null, description: null, text: 'Không nhắc tên người chết.', summary: null, reason: 'Đã lặp lại.' },
      { kind: 'AddSummary', name: null, description: null, text: null, summary: 'Một đêm mưa, chiếc thuyền trống.', reason: 'Tóm tắt tập vừa duyệt.' },
    ],
    preview: PREVIEW,
    ...overrides,
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Stub server
// ---------------------------------------------------------------------------------------------------------------

type Reply = (route: Route, request: Request, stub: BibleStub) => Promise<void> | void;

interface BibleStub {
  state: BibleWire;
  readonly gets: Request[];
  readonly puts: Request[];
  readonly accepts: Request[];
  readonly dismisses: Request[];
  readonly episodeGets: Request[];
  /** Overrides for the default handlers (conflict, validation, 409, title 404). */
  putReply?: Reply;
  acceptReply?: Reply;
  dismissReply?: Reply;
  episodeReply?: Reply;
}

/** The next server state: the stored content plus the committed `{ version, etag }` pair. */
function committed(state: BibleWire, content: BibleContentWire, proposal: BibleProposalWire | null): BibleWire {
  const version = state.version + 1;
  return { ...state, version, etag: `"sb-${version}"`, content: { ...content, schema_version: 'bible.v1' }, proposal };
}

const first = (requests: readonly Request[]): Request => {
  const request = requests[0];
  if (request === undefined) throw new Error('the expected request was never issued');
  return request;
};

async function stubBible(page: Page, initial: BibleWire): Promise<BibleStub> {
  const stub: BibleStub = { state: initial, gets: [], puts: [], accepts: [], dismisses: [], episodeGets: [] };

  await page.route(
    (url) => url.pathname === BIBLE_API,
    async (route) => {
      const request = route.request();
      if (request.method() === 'GET') {
        stub.gets.push(request);
        return fulfilJson(route, 200, stub.state, { ETag: stub.state.etag });
      }
      stub.puts.push(request);
      if (stub.putReply) return stub.putReply(route, request, stub);
      if (request.headers()['if-match'] !== stub.state.etag) {
        return fulfilJson(route, 412, problem(412, 'bible_conflict'), { 'Content-Type': 'application/problem+json' });
      }
      const body = request.postDataJSON() as { content: BibleContentWire };
      stub.state = committed(stub.state, body.content, stub.state.proposal);
      return fulfilJson(route, 200, { version: stub.state.version, etag: stub.state.etag }, { ETag: stub.state.etag });
    },
  );

  for (const [action, reply, recorded] of [
    ['accept', 'acceptReply', stub.accepts],
    ['dismiss', 'dismissReply', stub.dismisses],
  ] as const) {
    await page.route(
      (url) => url.pathname === `${BIBLE_API}/proposal/${action}`,
      async (route) => {
        const request = route.request();
        recorded.push(request);
        const override = stub[reply];
        if (override) return override(route, request, stub);
        if (action === 'dismiss') {
          // A dismissal writes no content version (02.6.3 §4 U3): 204, the proposal row gone, the version kept.
          stub.state = { ...stub.state, proposal: null };
          return route.fulfill({ status: 204 });
        }
        stub.state = committed(stub.state, stub.state.content, null);
        return fulfilJson(route, 200, { version: stub.state.version, etag: stub.state.etag }, { ETag: stub.state.etag });
      },
    );
  }

  await page.route(
    (url) => url.pathname.startsWith('/api/v1/episodes/'),
    async (route) => {
      const request = route.request();
      stub.episodeGets.push(request);
      if (stub.episodeReply) return stub.episodeReply(route, request, stub);
      return fulfilJson(route, 200, { id: PROPOSING_EPISODE, title: PROPOSING_TITLE });
    },
  );

  return stub;
}

/** Boots the stubbed session, the bible endpoints and lands on the page in `lang`. */
async function openBible(page: Page, initial: BibleWire, lang: E2eLang = 'vi'): Promise<BibleStub> {
  await stubSession(page, lang);
  const stub = await stubBible(page, initial);
  await page.goto(BIBLE_PATH);
  await expect(page.locator('html')).toHaveAttribute('lang', lang);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(COPY[lang].title);
  return stub;
}

// ---------------------------------------------------------------------------------------------------------------
// Page helpers
// ---------------------------------------------------------------------------------------------------------------

const saveButton = (page: Page, lang: E2eLang) => page.getByRole('button', { name: COPY[lang].save, exact: true });

/** The characters section, found by its legend instead of its position, and that section's first name input. */
function firstCharacterName(page: Page, lang: E2eLang) {
  const entry = page
    .locator('nts-bible-section')
    .filter({ has: page.locator('legend', { hasText: COPY[lang].characters }) })
    .locator('.entry')
    .first();
  return entry.getByLabel(COPY[lang].entry.name, { exact: true });
}

const versionText = (lang: E2eLang, version: number) => COPY[lang].version.replace('{{version}}', String(version));

async function expectBibleLoaded(page: Page, lang: E2eLang, version: number): Promise<void> {
  const c = COPY[lang];
  await expect(page.locator('.version')).toHaveText(versionText(lang, version));
  await expect(page.locator('fieldset.narrator legend')).toContainText(c.narrator);
}

/** Presses Tab from the document body until `selector` owns the focus (keyboard-only proof). */
async function tabUntil(page: Page, selector: string, limit = 160): Promise<void> {
  for (let step = 0; step < limit; step += 1) {
    await page.keyboard.press('Tab');
    const focused = await page.evaluate((value) => document.activeElement?.matches(value) ?? false, selector);
    if (focused) return;
  }
  throw new Error(`focus never reached ${selector} within ${limit} Tab presses`);
}

// ---------------------------------------------------------------------------------------------------------------
// 1. Load, edit, save, adopt, persist
// ---------------------------------------------------------------------------------------------------------------

test('Load_EditCharacter_SaveSnakeCaseWithIfMatch_ThenReloadPersists', async ({ page }) => {
  const stub = await openBible(page, bibleWire());
  await expectBibleLoaded(page, 'vi', 1);

  // Load: exactly one GET, and five sections rendered from the server's data.
  await expect.poll(() => stub.gets.length).toBe(1);
  await expect(page.locator('nts-bible-section')).toHaveCount(4);
  for (const [index, label] of [L.characters, L.places, L.rules, L.summaries].entries()) {
    await expect(page.locator('nts-bible-section').nth(index).locator('legend')).toContainText(label);
  }
  await expect(page.locator('fieldset.narrator').getByLabel(L['narrator.name'], { exact: true })).toHaveValue('Người kể chính');
  await expect(firstCharacterName(page, 'vi')).toHaveValue('Bà Tư');
  await expect(
    page
      .locator('nts-bible-section')
      .filter({ has: page.locator('legend', { hasText: L.characters }) })
      .getByLabel(L.entry.description, { exact: true }),
  ).toHaveValue('Người giữ đền.');

  // Edit a character name: the dirty state is the only thing that enables Save.
  await expect(saveButton(page, 'vi')).toBeDisabled();
  await firstCharacterName(page, 'vi').fill('Bà Tư đã đổi');
  await expect(saveButton(page, 'vi')).toBeEnabled();
  await saveButton(page, 'vi').click();

  await expect.poll(() => stub.puts.length).toBe(1);
  const put = first(stub.puts);
  expect(put.headers()['if-match'], 'the loaded ETag is echoed on the only write').toBe('"sb-1"');

  const body = put.postDataJSON() as { content: Record<string, unknown> };
  expect(Object.keys(body), 'the body is exactly `{ content }`').toEqual(['content']);
  const content = body.content;
  expect(Object.keys(content).sort(), 'snake_case bible.v1, no schema_version').toEqual([
    'episode_summaries',
    'narrator',
    'places',
    'recurring_characters',
    'rules',
  ]);
  expect(content).not.toHaveProperty('schema_version');
  expect(content['narrator']).toEqual({ name: 'Người kể chính', persona: 'Giọng kể khàn, chậm.', voice_notes: 'Ngắt nghỉ sau mỗi câu.' });
  expect((content['recurring_characters'] as readonly Record<string, unknown>[])[0]).toEqual({
    id: 'ch1',
    name: 'Bà Tư đã đổi',
    description: 'Người giữ đền.',
    first_episode_id: OTHER_EPISODE,
  });
  expect((content['rules'] as readonly Record<string, unknown>[])[0]).toEqual({ id: 'rl1', text: 'Không ai được gọi tên người chết.' });
  // `structure`/`approved_at` are echoed on load and must not travel back.
  expect((content['episode_summaries'] as readonly Record<string, unknown>[])[0]).toEqual({
    episode_id: OTHER_EPISODE,
    title: 'Tập một',
    summary: 'Một đêm mưa.',
  });
  const serialised = JSON.stringify(body);
  expect(serialised).not.toContain('structure');
  expect(serialised).not.toContain('approved_at');
  expect(serialised).not.toContain('schema_version');

  // The response is adopted (version 2 from the refetch) and the announcement is polite.
  await expect.poll(() => stub.gets.length).toBe(2);
  await expectBibleLoaded(page, 'vi', 2);
  await expect(page.locator('.saved')).toHaveText(L.saved);
  await expect(firstCharacterName(page, 'vi')).toHaveValue('Bà Tư đã đổi');

  // Reload the page: the stub answers the saved content, so the edit persisted.
  const getsBeforeReload = stub.gets.length;
  await page.reload();
  await expectBibleLoaded(page, 'vi', 2);
  await expect(firstCharacterName(page, 'vi')).toHaveValue('Bà Tư đã đổi');
  await expect.poll(() => stub.gets.length).toBe(getsBeforeReload + 1);
});

// ---------------------------------------------------------------------------------------------------------------
// 2. 412 conflict: copy + Reload action, edits kept, refetch adopts the newer content
// ---------------------------------------------------------------------------------------------------------------

test('Conflict_412_KeepsEdits_ReloadAdoptsServerState', async ({ page }) => {
  const stub = await openBible(page, bibleWire());
  stub.putReply = (route) => fulfilJson(route, 412, problem(412, 'bible_conflict'), { 'Content-Type': 'application/problem+json' });

  await firstCharacterName(page, 'vi').fill('Bản chưa lưu');
  await expect(saveButton(page, 'vi')).toBeEnabled();
  await saveButton(page, 'vi').click();

  const alert = page.locator('.conflict[role=alert]');
  await expect(alert).toContainText(L.error.conflict);
  await expect(alert.getByRole('button', { name: L.conflict.reload, exact: true })).toBeVisible();
  await expect(firstCharacterName(page, 'vi'), 'the edited value stays until an explicit reload').toHaveValue('Bản chưa lưu');
  await expect(saveButton(page, 'vi'), 'a conflicting form is not saveable').toBeDisabled();

  // The server moved on: the next GET answers the newer entity.
  stub.state = bibleWire({ version: 2, etag: '"sb-2"', content: bibleContent({ recurring_characters: [{ id: 'ch1', name: 'Bà Tư máy chủ', description: 'Người giữ đền.', first_episode_id: OTHER_EPISODE }] }) });
  await alert.getByRole('button', { name: L.conflict.reload, exact: true }).click();

  await expect(page.locator('.conflict')).toHaveCount(0);
  await expectBibleLoaded(page, 'vi', 2);
  await expect(firstCharacterName(page, 'vi')).toHaveValue('Bà Tư máy chủ');
  await expect.poll(() => stub.gets.length).toBe(2);
});

// ---------------------------------------------------------------------------------------------------------------
// 3. 400 bible_invalid_content: field marked + form-level alert for the unmapped pointer
// ---------------------------------------------------------------------------------------------------------------

test('InvalidContent_400_MarksTheField_AndAlertsTheUnmappedKey', async ({ page }) => {
  const stub = await openBible(page, bibleWire());
  stub.putReply = (route) =>
    fulfilJson(
      route,
      400,
      problem(400, 'bible_invalid_content', {
        errors: { 'recurring_characters[0].name': ['too_long'], mystery_pointer: ['invalid'] },
      }),
      { 'Content-Type': 'application/problem+json' },
    );

  await firstCharacterName(page, 'vi').fill('Tên quá dài do máy chủ từ chối');
  await saveButton(page, 'vi').click();

  const charactersSection = page.locator('nts-bible-section').filter({ has: page.locator('legend', { hasText: L.characters }) });
  // The page re-binds each bound control to the wire code's field copy (`series.bible.field.<code>`).
  await expect(charactersSection.locator('.entry').first().locator('mat-error')).toHaveText(L.field.too_long);
  await expect(page.locator('.field-issues[role=alert]')).toContainText(L.error.limits.replace('{{field}}', L.characters));
  await expect(page.locator('.field-issues[role=alert]')).toContainText(L.field.too_long);
  const formAlert = page.locator('nts-form-error-alert');
  await expect(formAlert).toHaveAttribute('role', 'alert');
  await expect(formAlert).toContainText(ERR.bible_invalid_content);
  await expect(formAlert).toContainText(ERR.form.unmappedCount.replace('{{count}}', '1'));
  await expect.poll(() => stub.puts.length).toBe(1);
});

// ---------------------------------------------------------------------------------------------------------------
// 4. Proposal: title lookup, banner counts, deferred diff, Accept / Dismiss / 409
// ---------------------------------------------------------------------------------------------------------------

async function openProposal(page: Page): Promise<BibleStub> {
  const stub = await openBible(page, bibleWire({ proposal: proposalWire() }));
  const banner = page.locator('nts-bible-proposal-banner');
  await expect(banner.locator('section.proposal')).toBeVisible();
  await expect(banner.locator('h2')).toHaveText(
    L.proposal.banner.replace('{{n}}', '4').replace('{{title}}', PROPOSING_TITLE),
  );
  await expect.poll(() => stub.episodeGets.length).toBe(1);
  expect(new URL(first(stub.episodeGets).url()).pathname).toBe(`/api/v1/episodes/${PROPOSING_EPISODE}`);
  return stub;
}

test('Proposal_RendersCountsAndDeferredDiff_AcceptPostsWithoutEtagOrBody', async ({ page }) => {
  const stub = await openProposal(page);
  const banner = page.locator('nts-bible-proposal-banner');

  // Server-computed preview counts.
  await expect(banner.locator('.proposal__counts dd')).toHaveText(['2', '1', '3']);

  // The rows live behind `@defer (on viewport)`; bring the region into view and assert the mapped DiffRows.
  await banner.locator('.proposal__diff').scrollIntoViewIfNeeded();
  const rows = banner.locator('.diff__row');
  await expect(rows).toHaveCount(4);
  await expect(banner.getByRole('region', { name: L.proposal.diffLabel })).toBeVisible();
  await expect(rows.nth(0).locator('dt')).toHaveText(L.op.character);
  await expect(rows.nth(1).locator('dt')).toHaveText(L.op.place);
  await expect(rows.nth(2).locator('dt')).toHaveText(L.op.rule);
  await expect(rows.nth(3).locator('dt')).toHaveText(L.op.summary);
  for (let index = 0; index < 4; index += 1) await expect(rows.nth(index)).toHaveAttribute('data-kind', 'add');
  await expect(rows.nth(0).locator('ins')).toContainText('Ông Ba');
  await expect(rows.nth(0).locator('.diff__reason')).toHaveText(`${L.proposal.more}: Xuất hiện ở tập mới.`);
  await expect(rows.nth(3).locator('ins')).toContainText('Một đêm mưa, chiếc thuyền trống.');
  await expectA11yClean(page);

  // DOM order: Accept precedes Dismiss; the same order holds for the keyboard.
  const actions = banner.locator('.proposal__actions button');
  expect((await actions.allTextContents()).map((label) => label.trim())).toEqual([L.proposal.accept, L.proposal.dismiss]);
  await actions.nth(0).focus();
  await page.keyboard.press('Tab');
  expect(await page.evaluate(() => document.activeElement?.textContent?.trim() ?? '')).toBe(L.proposal.dismiss);

  // Accept: no body, no If-Match, then a refetch that drops the proposal.
  stub.acceptReply = (route) => {
    stub.state = committed(stub.state, stub.state.content, null);
    return fulfilJson(route, 200, { version: stub.state.version, etag: stub.state.etag }, { ETag: stub.state.etag });
  };
  await actions.nth(0).click();
  await expect.poll(() => stub.accepts.length).toBe(1);
  const accept = first(stub.accepts);
  expect(new URL(accept.url()).pathname).toBe(`${BIBLE_API}/proposal/accept`);
  expect(accept.headers()['if-match']).toBeUndefined();
  expect(accept.postData() ?? '').toBe('');

  await expect(banner).toHaveCount(0);
  await expectBibleLoaded(page, 'vi', 2);
  await expect.poll(() => stub.gets.length).toBe(2);
  await expectA11yClean(page);
});

test('Proposal_Dismiss_ThroughConfirmDialog_PostsAndHidesTheBanner', async ({ page }) => {
  const stub = await openProposal(page);
  stub.dismissReply = (route) => {
    stub.state = { ...stub.state, proposal: null };
    return route.fulfill({ status: 204 });
  };

  await page.locator('nts-bible-proposal-banner').getByRole('button', { name: L.proposal.dismiss, exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(L.proposal.dismissTitle);
  await dialog.getByRole('button', { name: L.proposal.dismiss, exact: true }).click();

  await expect.poll(() => stub.dismisses.length).toBe(1);
  const dismiss = first(stub.dismisses);
  expect(new URL(dismiss.url()).pathname).toBe(`${BIBLE_API}/proposal/dismiss`);
  expect(dismiss.headers()['if-match']).toBeUndefined();
  expect(dismiss.postData() ?? '').toBe('');

  await expect(page.locator('nts-bible-proposal-banner')).toHaveCount(0);
  await expectBibleLoaded(page, 'vi', 1);
  await expect.poll(() => stub.gets.length).toBe(2);
});

test('Proposal_409_NotPending_ShowsGoneCopyAndRefetches', async ({ page }) => {
  const stub = await openProposal(page);
  stub.acceptReply = (route) => {
    stub.state = committed(stub.state, stub.state.content, null);
    return fulfilJson(route, 409, problem(409, 'bible_proposal_not_pending'), { 'Content-Type': 'application/problem+json' });
  };

  await page.locator('nts-bible-proposal-banner').getByRole('button', { name: L.proposal.accept, exact: true }).click();

  await expect(page.getByText(L.proposal.none, { exact: true })).toBeVisible();
  await expect(page.locator('nts-bible-proposal-banner')).toHaveCount(0);
  await expectBibleLoaded(page, 'vi', 2);
  await expect.poll(() => stub.gets.length).toBe(2);
  await expect(page.locator('nts-form-error-alert[role=alert]'), 'the 409 is copy, not a page error').toHaveCount(0);
});

// ---------------------------------------------------------------------------------------------------------------
// 5. No proposal -> no episode request; a 404 title falls back without error state or retry
// ---------------------------------------------------------------------------------------------------------------

test('NoProposal_IssuesNoEpisodeRequest', async ({ page }) => {
  const stub = await openBible(page, bibleWire());
  await expectBibleLoaded(page, 'vi', 1);
  await expect(page.locator('nts-bible-proposal-banner')).toHaveCount(0);
  await page.waitForTimeout(500);
  expect(stub.episodeGets, 'a bible without a proposal never asks for an episode title').toHaveLength(0);
});

test('Proposal_TitleLookup404_FallsBackWithoutErrorOrRetry', async ({ page }) => {
  await stubSession(page, 'vi');
  const stub = await stubBible(page, bibleWire({ proposal: proposalWire() }));
  stub.episodeReply = (route) => fulfilJson(route, 404, problem(404, 'episode_not_found'), { 'Content-Type': 'application/problem+json' });

  await page.goto(BIBLE_PATH);
  const banner = page.locator('nts-bible-proposal-banner');
  await expect(banner.locator('section.proposal')).toBeVisible();
  await expect(banner.locator('h2')).toHaveText(L.proposal.bannerNoTitle.replace('{{n}}', '4'));
  await expect(page.locator('nts-error-state')).toHaveCount(0);
  await page.waitForTimeout(500);
  expect(stub.episodeGets, 'the failed lookup is never retried').toHaveLength(1);
});

// ---------------------------------------------------------------------------------------------------------------
// 6. Idle: no polling and no realtime fetch for 60 s
// ---------------------------------------------------------------------------------------------------------------

test('Idle60s_MakesNoFurtherApiRequest', async ({ page }) => {
  // The 60 s hold plus the boot exceeds the 30 s default; the explicit budget documents the requirement.
  test.setTimeout(150_000);
  const apiRequests: string[] = [];
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/api/v1/')) apiRequests.push(`${request.method()} ${url.pathname}`);
  });

  await stubSession(page, 'vi');
  // The shell's realtime service retries a failing ticket on a 1-30 s backoff. Pin that one attempt open so the
  // strict "no new /api/v1/ request" assertion measures the bible page, not the reconnect ladder.
  await page.route('**/api/v1/hubs/ticket', () => new Promise<void>(() => undefined));
  const stub = await stubBible(page, bibleWire());
  await page.goto(BIBLE_PATH);
  await expectBibleLoaded(page, 'vi', 1);
  await expect.poll(() => stub.gets.length).toBe(1);
  // The realtime attach is the last boot request; wait for it so the baseline is a complete boot picture.
  await expect.poll(() => apiRequests.filter((entry) => entry.endsWith(' /api/v1/hubs/ticket')).length).toBe(1);
  await page.waitForTimeout(250);

  const requestsBefore = [...apiRequests];
  const bibleGetsBefore = stub.gets.length;
  await page.waitForTimeout(60_000);
  expect(apiRequests, 'no periodic fetch, no realtime refetch while idle').toEqual(requestsBefore);
  expect(stub.gets.length, 'the bible is not polled').toBe(bibleGetsBefore);
});

// ---------------------------------------------------------------------------------------------------------------
// 7. axe with and without a proposal; keyboard-only edit and save
// ---------------------------------------------------------------------------------------------------------------

test('Axe_IsCleanWithoutAProposal', async ({ page }) => {
  await openBible(page, bibleWire());
  await expectBibleLoaded(page, 'vi', 1);
  await expectA11yClean(page);
});

test('Axe_IsCleanWithAProposal', async ({ page }) => {
  const stub = await openProposal(page);
  await page.locator('nts-bible-proposal-banner .proposal__diff').scrollIntoViewIfNeeded();
  await expect(page.locator('nts-bible-proposal-banner .diff__row')).toHaveCount(4);
  await expectA11yClean(page);
  await expect.poll(() => stub.episodeGets.length).toBe(1);
});

test('KeyboardOnly_TabToAField_Edit_ThenReachAndActivateSave', async ({ page }) => {
  // Narrator only: the shortest stable tab order for the keyboard walk.
  const stub = await openBible(page, bibleWire({ content: bibleContent({ recurring_characters: [], places: [], rules: [], episode_summaries: [] }) }));
  await expectBibleLoaded(page, 'vi', 1);

  // From the document body, Tab reaches the narrator name input without any pointer interaction.
  await tabUntil(page, 'fieldset.narrator input');
  await expect(page.locator('fieldset.narrator input')).toHaveValue('Người kể chính');
  // Keyboard-only edit: select the field's text and replace it in one insertion (no pointer, no paste).
  await page.keyboard.press('ControlOrMeta+a');
  await page.keyboard.insertText('Người kể chính thứ hai');
  await expect(page.locator('fieldset.narrator input')).toHaveValue('Người kể chính thứ hai');
  const save = saveButton(page, 'vi');
  await expect(save).toBeEnabled();

  // The header Save precedes the form in DOM order, so Shift+Tab returns straight to it.
  await page.keyboard.press('Shift+Tab');
  expect(await page.evaluate(() => document.activeElement?.getAttribute('form') ?? '')).toBe('bible-form');
  await page.keyboard.press('Enter');

  await expect.poll(() => stub.puts.length).toBe(1);
  const body = first(stub.puts).postDataJSON() as { content: { narrator: { name: string } } };
  expect(body.content.narrator.name).toBe('Người kể chính thứ hai');
  await expect(page.locator('.saved')).toHaveText(L.saved);
});

// ---------------------------------------------------------------------------------------------------------------
// 8. vi + en render
// ---------------------------------------------------------------------------------------------------------------

for (const lang of ['vi', 'en'] as const) {
  test.describe(`bible ${lang}`, () => {
    test.use({ locale: lang === 'vi' ? 'vi-VN' : 'en-US' });

    test('renders every section in the active language', async ({ page }) => {
      const c = COPY[lang];
      await openBible(page, bibleWire(), lang);
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(c.title);
      await expect(page.locator('fieldset.narrator legend')).toContainText(c.narrator);
      for (const [index, label] of [c.characters, c.places, c.rules, c.summaries].entries()) {
        await expect(page.locator('nts-bible-section').nth(index).locator('legend')).toContainText(label);
      }
      await expect(saveButton(page, lang)).toBeVisible();
      await expect(page.getByLabel(c['narrator.persona'], { exact: true })).toHaveValue('Giọng kể khàn, chậm.');
      await expect(firstCharacterName(page, lang)).toHaveValue('Bà Tư');
    });
  });
}
