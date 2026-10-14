import type { Locator, Page, Request, Route } from '@playwright/test';
import type { AttestationViewDto } from '../src/app/shared/api/models/attestation-view-dto';
import type { CurrentStoryDto } from '../src/app/shared/api/models/current-story-dto';
import type { EpisodeDetailDto } from '../src/app/shared/api/models/episode-detail-dto';
import type { QualityHintDto } from '../src/app/shared/api/models/quality-hint-dto';
import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { EPISODE_ID } from './fixtures/episodes-stubs';
import { fulfilJson, problem, type E2eLang } from './fixtures/series-stubs';
import {
  EDITOR_COPY, EDITOR_PATH, FRESH_STORY_ETAG, FRESH_VERSION_ID, STORED_STORY_ETAG, STORED_VERSION_ID, STORY_CONTENT,
  editorDetail, expectEditor, segment, storySnapshot, stubStoryEditor, type StoryEditorStubs,
} from './fixtures/story-editor-stubs';
import coreEn from '../src/assets/i18n/core/en.json' with { type: 'json' };
import coreVi from '../src/assets/i18n/core/vi.json' with { type: 'json' };
import episodesEn from '../src/assets/i18n/episodes/en.json' with { type: 'json' };
import episodesVi from '../src/assets/i18n/episodes/vi.json' with { type: 'json' };
import errorsEn from '../src/assets/i18n/errors/en.json' with { type: 'json' };
import errorsVi from '../src/assets/i18n/errors/vi.json' with { type: 'json' };

// 10.5.6-T1 approve / reopen dialogs + true-crime attestation (stubbed `page.route`, no backend): the approve command
// writes exactly one POST with the live If-Match and no `attestation` field on a plain episode, the true-crime block
// gates the confirm until the tick and echoes only the text version, the 409/412/400 refusals map to their own
// regions (never raw `detail`), a reopen sends its own POST and hands the script back to the editor, and the dialog
// is axe-clean, focus-trapped and reachable at 360 px — in vi and en. Real compose acceptance stays with 11.5.1-T1.
//
// The shipped copy is read from the bundles (`src/assets/i18n/**`), never re-typed: `episodes.editor.action.*` names
// the launcher, `episodes.approve.*` / `episodes.reopen.*` the dialogs, `errors.<code>` the refusal copy and
// `core.ui.*` the shared chrome, so a label change fails this spec instead of drifting.

/** The dialog + launcher copy of the `episodes` scope (only the leaves this spec matches on). */
interface EpisodesCopy {
  readonly editor: { readonly action: { readonly approve_story: string; readonly reopen_story: string } };
  readonly approve: {
    readonly title: string;
    readonly body: string;
    readonly note: string;
    readonly hint: string;
    readonly confirm: string;
    readonly done: string;
    readonly attestation: { readonly accept: string; readonly legend: string };
  };
  readonly reopen: { readonly title: string; readonly body: string; readonly confirm: string; readonly done: string };
}

/** The `errors` scope leaves the approve/reopen refusals and the conflict dialog render. */
interface ApprovalErrorsCopy {
  readonly episode_invalid_state: string;
  readonly story_fact_sheet_outdated: string;
  readonly story_moderated: string;
  readonly story_attestation_required: string;
  readonly keywords_flagged: string;
  readonly conflict: { readonly title: string; readonly reload: string; readonly saveAsNew: string; readonly cancel: string };
}

/** The shared (`core`) chrome: the status chip's accessible name and the confirm dialog's cancel label. */
interface CoreChromeCopy {
  readonly core: {
    readonly ui: {
      readonly statusChip: Readonly<Record<string, string>>;
      readonly confirm: { readonly cancel: string };
    };
  };
}

const COPY: Readonly<Record<E2eLang, EpisodesCopy>> = {
  vi: episodesVi as unknown as EpisodesCopy,
  en: episodesEn as unknown as EpisodesCopy,
};
const ERRORS: Readonly<Record<E2eLang, ApprovalErrorsCopy>> = {
  vi: errorsVi as unknown as ApprovalErrorsCopy,
  en: errorsEn as unknown as ApprovalErrorsCopy,
};
const CORE: Readonly<Record<E2eLang, CoreChromeCopy>> = {
  vi: coreVi as unknown as CoreChromeCopy,
  en: coreEn as unknown as CoreChromeCopy,
};

const APPROVE_PATH = `/api/v1/episodes/${EPISODE_ID}/story/approve`;
const REOPEN_PATH = `/api/v1/episodes/${EPISODE_ID}/story/reopen`;
/** A server detail that must never reach the DOM: every refusal shows mapped copy, never the body's prose. */
const PRIVATE_DETAIL = 'PRIVATE SERVER DETAIL MUST NOT BE RENDERED';

/** The server-owned true-crime statement: two paragraphs, the second one carrying literal markup characters. */
const ATTESTATION_PARAGRAPH_1 = 'Kịch bản tách dữ kiện đã kiểm chứng khỏi suy đoán.';
const ATTESTATION_PARAGRAPH_2 = 'Không kết luận tội danh ngoài <b>bản án công khai</b>.';
const ATTESTATION_TEXT = `${ATTESTATION_PARAGRAPH_1}\n\n${ATTESTATION_PARAGRAPH_2}`;
const ATTESTATION: AttestationViewDto = {
  required: true, textVersion: 'ta.v1', text: ATTESTATION_TEXT, acceptedAt: null,
};
/** The chip on `p:1` (the second paragraph, 0-based address; it carries two keywords, so index 1 exists) that the refused approve points at. */
const FLAG_ROW = { segment: 'p:1', index: 1, rule: 'keyword_name_detected' };
const FLAG_ID = 'ann-p-1-keyword_name_detected-k1';

const HINTS: readonly QualityHintDto[] = [
  {
    ruleId: 'keyword_count_per_segment', messageKey: 'quality.keyword_count_per_segment', evidence: 'Paragraph 2 has 3 scenes',
    kind: 'objective', severity: 'blocking', segment: 'p:1', repairDirection: 'Keep at most two scenes', measured: null, threshold: null,
  },
  {
    ruleId: 'hook_missing', messageKey: 'quality.hook_missing', evidence: 'The hook states no question', kind: 'objective',
    severity: 'advisory', segment: 'hook', repairDirection: null, measured: null, threshold: null,
  },
];

/** The current story of one episode state: the fixture's snapshot plus the two fields 10.5.6-T1 reads. */
const scriptAt = (
  versionId: string,
  version: number,
  attestation: AttestationViewDto | null = null,
  qualityHints: readonly QualityHintDto[] = [],
): CurrentStoryDto => ({ ...storySnapshot(STORY_CONTENT, versionId, version), attestation, qualityHints: [...qualityHints] });

/** A plain (non-true-crime) script in review: no attestation block, the launcher is the four-condition WR6 gate. */
const REVIEW_DETAIL = (): EpisodeDetailDto => editorDetail({ currentStory: scriptAt(STORED_VERSION_ID, 1) });
/** The same script for a true-crime episode: the server sent a required statement the user has not accepted yet. */
const TRUE_CRIME_DETAIL = (): EpisodeDetailDto =>
  editorDetail({ currentStory: scriptAt(STORED_VERSION_ID, 1, ATTESTATION) });
/** A script in review whose saved version carries quality hints: advisory only, they never lock approve. */
const HINTS_DETAIL = (): EpisodeDetailDto => editorDetail({ currentStory: scriptAt(STORED_VERSION_ID, 1, null, HINTS) });
/** An approved script: the editor is read-only (`edit_story` is gone from nextActions) and reopen is the one command. */
const APPROVED_DETAIL = (): EpisodeDetailDto => editorDetail({
  status: 'story_approved', nextActions: ['render', 'reopen_story'],
  currentStory: scriptAt(FRESH_VERSION_ID, 2),
});
/** What the server answers to a reopen: the same version, back in review and editable again. */
const REOPENED_DETAIL = (): EpisodeDetailDto => editorDetail({
  status: 'story_review', nextActions: ['edit_story', 'approve_story'],
  currentStory: scriptAt(FRESH_VERSION_ID, 2),
});

/** One scripted command answer: a 200 carrying the new state, or a problem body plus the detail the GET then serves. */
interface ApprovalReply {
  readonly status: number;
  readonly code?: string;
  readonly errors?: readonly Readonly<Record<string, unknown>>[];
  /** The state the episode is in once this answer landed; the follow-up detail read serves it. */
  readonly nextDetail?: EpisodeDetailDto;
}

interface ApprovalOptions {
  readonly detail: EpisodeDetailDto;
  readonly approve?: ApprovalReply;
  readonly reopen?: ApprovalReply;
  readonly theme?: 'light' | 'dark';
}

interface ApprovalStubs {
  readonly editor: StoryEditorStubs;
  readonly approvePosts: Request[];
  readonly reopenPosts: Request[];
  /** Every `GET /api/v1/episodes/{id}` this fixture answered (the command's own refetch included). */
  readonly detailGets: Request[];
}

/** The `ApprovedStoryDto` the accept endpoint answers (the task's pinned shape, derived from the state it moved to). */
const approvedDto = (detail: EpisodeDetailDto): Record<string, unknown> => ({
  approvedAt: '2026-10-14T10:00:00Z', episodeId: EPISODE_ID, etag: detail.etag, status: detail.status,
  storyVersionId: detail.currentStory?.versionId ?? '',
});
/** The `EpisodeStoryStateDto` the reopen endpoint answers. */
const storyStateDto = (detail: EpisodeDetailDto): Record<string, unknown> => ({
  episodeId: EPISODE_ID, etag: detail.etag, status: detail.status, storyVersionId: detail.currentStory?.versionId ?? '',
});

/**
 * The episode detail + the two story commands of 10.5.6-T1, on top of `stubStoryEditor` (session, series, list, save).
 * The routes are registered after the shared ones, so this fixture's detail GET wins over the shared stub's; a 200
 * moves the served detail to the state that command left behind, exactly as the endpoints do.
 */
async function stubApproval(page: Page, lang: E2eLang, options: ApprovalOptions): Promise<ApprovalStubs> {
  const editor = await stubStoryEditor(page, lang, { detail: options.detail });
  let detail = options.detail;
  const approvePosts: Request[] = [];
  const reopenPosts: Request[] = [];
  const detailGets: Request[] = [];

  const answer = async (route: Route, reply: ApprovalReply, ok: () => Record<string, unknown>): Promise<void> => {
    if (reply.status === 200) {
      if (reply.nextDetail !== undefined) detail = reply.nextDetail;
      await fulfilJson(route, 200, ok(), { ETag: detail.etag });
      return;
    }
    await fulfilJson(route, reply.status, problem(reply.status, reply.code ?? 'unknown_error', {
      ...(reply.errors === undefined ? {} : { errors: reply.errors }),
      detail: PRIVATE_DETAIL,
    }), { 'Content-Type': 'application/problem+json' });
  };

  await page.route(`**${APPROVE_PATH}`, async (route) => {
    const request = route.request();
    expect(request.method()).toBe('POST');
    approvePosts.push(request);
    await answer(route, options.approve ?? { status: 200, nextDetail: options.detail }, () => approvedDto(detail));
  });
  await page.route(`**${REOPEN_PATH}`, async (route) => {
    const request = route.request();
    expect(request.method()).toBe('POST');
    reopenPosts.push(request);
    await answer(route, options.reopen ?? { status: 200, nextDetail: options.detail }, () => storyStateDto(detail));
  });
  // Registered last: this read is the page's one truth about the current status and ETag.
  await page.route(`**/api/v1/episodes/${EPISODE_ID}`, async (route) => {
    const request = route.request();
    expect(request.method()).toBe('GET');
    detailGets.push(request);
    await fulfilJson(route, 200, detail, { ETag: detail.etag });
  });
  return { editor, approvePosts, reopenPosts, detailGets };
}

async function openApproval(page: Page, lang: E2eLang, options: ApprovalOptions): Promise<ApprovalStubs> {
  if (options.theme !== undefined) {
    // PreferencesStore storage key and shape (core/prefs/preferences.store.ts); no app hook needed.
    await page.addInitScript((theme) => {
      localStorage.setItem('nts.prefs.v1', JSON.stringify({ theme }));
    }, options.theme);
  }
  const stubs = await stubApproval(page, lang, options);
  await page.goto(EDITOR_PATH);
  await expectEditor(page, lang);
  await expect.poll(stubs.editor.connected).toBe(1);
  if (options.theme !== undefined) await expect(page.locator('html')).toHaveAttribute('data-theme', options.theme);
  return stubs;
}

/** Fills a Transloco `{{name}}` template with the values the component interpolates. */
function template(text: string, values: Readonly<Record<string, string | number>>): string {
  return Object.entries(values).reduce((result, [key, value]) => result.replace(`{{${key}}}`, String(value)), text);
}

const approveName = (lang: E2eLang): string => COPY[lang].editor.action.approve_story;
const reopenName = (lang: E2eLang): string => COPY[lang].editor.action.reopen_story;
const cancelLabel = (lang: E2eLang): string => CORE[lang].core.ui.confirm.cancel;
const statusLabel = (lang: E2eLang, status: string): string => CORE[lang].core.ui.statusChip[status] ?? status;

/** The launcher, by its accessible name and by whether it is enabled: the two never have to be guessed. */
const button = (page: Page, name: string, disabled: boolean): Locator =>
  page.getByRole('button', { name, exact: true, disabled });
const confirmIn = (dialog: Locator, name: string): Locator => dialog.getByRole('button', { name, exact: true });
const approveDialog = (page: Page, lang: E2eLang): Locator =>
  page.getByRole('dialog', { name: COPY[lang].approve.title, exact: true });
const reopenDialog = (page: Page, lang: E2eLang): Locator =>
  page.getByRole('dialog', { name: COPY[lang].reopen.title, exact: true });
const conflictDialog = (page: Page, lang: E2eLang): Locator =>
  page.getByRole('dialog', { name: ERRORS[lang].conflict.title, exact: true });
const alertIn = (dialog: Locator): Locator => dialog.getByRole('alert');
const statusChip = (page: Page, lang: E2eLang, status: string): Locator =>
  page.getByRole('img', { name: statusLabel(lang, status), exact: true });
const attestationBlock = (dialog: Locator): Locator => dialog.locator('nts-attestation-block');
const flagChip = (page: Page): Locator => page.locator(`#${FLAG_ID}`);
/** The editor field for a segment label: the read-only state is asserted on the manuscript itself. */
const titleField = (page: Page, lang: E2eLang): Locator => segment(page, EDITOR_COPY[lang].title);

/**
 * Records every text the page publishes to a polite live region, whichever region it is: the page's own
 * `role="status"`, CDK's announcer element and the editor's annotation region are all watched, so the assertion does
 * not depend on which one the implementation picked (nor on how soon it is cleared).
 */
async function recordAnnouncements(page: Page): Promise<void> {
  await page.evaluate(() => {
    const seen: string[] = [];
    Reflect.set(window, '__ntsApprovalAnnouncements', seen);
    const snapshot = (): void => {
      const live = '[role="status"], .cdk-live-announcer-element, .story-editor__annotation-announcement';
      for (const node of document.querySelectorAll(live)) {
        const text = (node.textContent ?? '').trim();
        if (text !== '' && !seen.includes(text)) seen.push(text);
      }
    };
    new MutationObserver(snapshot).observe(document.body, { childList: true, characterData: true, subtree: true });
    snapshot();
  });
}

const announcements = (page: Page): Promise<string[]> =>
  page.evaluate(() => (Reflect.get(window, '__ntsApprovalAnnouncements') as string[] | undefined) ?? []);

/** The words that would mean a phase-P dialog grew a cost line the approved deck never asked for. */
const COST_WORDS = ['credit', 'credits', 'cost', 'price', 'usd', 'vnđ', '$', '₫'] as const;

async function expectNoCostCopy(host: Locator): Promise<void> {
  const text = (await host.innerText()).toLowerCase();
  for (const word of COST_WORDS) expect(text, `approval dialog mentions "${word}"`).not.toContain(word);
}

/** Opens the approve dialog from its launcher and returns the dialog. */
async function openApproveDialog(page: Page, lang: E2eLang): Promise<Locator> {
  const launcher = button(page, approveName(lang), false);
  await expect(launcher).toHaveCount(1);
  await expect(launcher).toBeEnabled();
  await launcher.click();
  const dialog = approveDialog(page, lang);
  await expect(dialog).toBeVisible();
  return dialog;
}

/**
 * A non-true-crime episode in review: the dialog opens, confirming sends exactly one POST with the live If-Match and
 * no `attestation` field, the dialog closes, the page shows the approved status and announces it politely.
 */
async function approvePlainEpisodeFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].approve;
  const stubs = await openApproval(page, lang, {
    detail: REVIEW_DETAIL(), approve: { status: 200, nextDetail: APPROVED_DETAIL() },
  });
  await recordAnnouncements(page);

  const dialog = await openApproveDialog(page, lang);
  await expect(dialog).toContainText(copy.title);
  await expect(dialog).toContainText(copy.body);
  await expect(attestationBlock(dialog)).toHaveCount(0);

  await confirmIn(dialog, copy.confirm).click();

  await expect.poll(() => stubs.approvePosts.length).toBe(1);
  const posted = stubs.approvePosts[0]!;
  expect(posted.method()).toBe('POST');
  expect(new URL(posted.url()).pathname).toBe(APPROVE_PATH);
  expect(posted.headers()['if-match']).toBe(STORED_STORY_ETAG);
  const raw = posted.postData();
  expect(raw ?? '', 'a plain episode must not send an attestation field').not.toContain('attestation');
  if (raw !== null) expect(Object.keys(JSON.parse(raw) as Record<string, unknown>)).not.toContain('attestation');

  await expect(dialog).toHaveCount(0);
  await expect(statusChip(page, lang, 'story_approved')).toBeVisible();
  await expect.poll(() => announcements(page)).toContain(copy.done);

  // One command, one write: a duplicate submit would be a second POST.
  await page.waitForTimeout(650);
  expect(stubs.approvePosts).toHaveLength(1);
}

/**
 * A true-crime episode: the confirm is disabled until the tick, the statement renders as literal text (no `<b>`
 * element inside the attestation), and the accepted body carries only the version of the text that was shown.
 */
async function approveTrueCrimeFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].approve;
  const stubs = await openApproval(page, lang, {
    detail: TRUE_CRIME_DETAIL(), approve: { status: 200, nextDetail: APPROVED_DETAIL() },
  });
  await recordAnnouncements(page);

  const dialog = await openApproveDialog(page, lang);
  const block = attestationBlock(dialog);
  await expect(block).toBeVisible();
  await expect(block).toContainText(ATTESTATION_PARAGRAPH_1);
  await expect(block).toContainText(ATTESTATION_PARAGRAPH_2);
  await expect(block.locator('b')).toHaveCount(0);
  expect(await block.evaluate((element) => element.innerHTML)).toContain('&lt;b&gt;bản án công khai&lt;/b&gt;');

  const confirm = confirmIn(dialog, copy.confirm);
  const checkbox = dialog.getByRole('checkbox', { name: copy.attestation.accept, exact: true });
  await expect(checkbox).not.toBeChecked();
  await expect(confirm).toBeDisabled();

  await checkbox.check();
  await expect(checkbox).toBeChecked();
  await expect(confirm).toBeEnabled();
  await confirm.click();

  await expect.poll(() => stubs.approvePosts.length).toBe(1);
  const posted = stubs.approvePosts[0]!;
  expect(posted.headers()['if-match']).toBe(STORED_STORY_ETAG);
  expect(posted.postDataJSON()).toEqual({ attestation: { accepted: true, textVersion: 'ta.v1' } });

  await expect(dialog).toHaveCount(0);
  await expect(statusChip(page, lang, 'story_approved')).toBeVisible();
  await expect.poll(() => announcements(page)).toContain(copy.done);
}

/**
 * A `409 keywords_flagged`: the dialog shows the mapped blocked copy (never the raw detail), the chip on the flagged
 * paragraph appears, and the command stays unavailable — the dialog's confirm and the launcher are both disabled.
 */
async function approveKeywordsFlaggedFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].approve;
  const stubs = await openApproval(page, lang, {
    detail: REVIEW_DETAIL(),
    approve: { status: 409, code: 'keywords_flagged', errors: [FLAG_ROW] },
  });

  const dialog = await openApproveDialog(page, lang);
  const confirm = confirmIn(dialog, copy.confirm);
  await confirm.click();

  await expect.poll(() => stubs.approvePosts.length).toBe(1);
  await expect(alertIn(dialog)).toContainText(ERRORS[lang].keywords_flagged);
  await expect(alertIn(dialog)).not.toContainText(PRIVATE_DETAIL);
  await expect(flagChip(page)).toBeVisible();
  await expect(confirm).toBeDisabled();

  // The refusal is remembered against the draft: closing the dialog does not hand the command back.
  await dialog.getByRole('button', { name: cancelLabel(lang), exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(button(page, approveName(lang), false)).toHaveCount(0);
  await expect(button(page, approveName(lang), true)).toHaveCount(1);
}

/** A `409 story_fact_sheet_outdated`: the dialog renders the fact-sheet copy and never the problem's detail. */
async function approveBlockedCodeFlow(page: Page, lang: E2eLang, code: 'story_fact_sheet_outdated' | 'story_moderated'): Promise<void> {
  const copy = COPY[lang].approve;
  const stubs = await openApproval(page, lang, { detail: REVIEW_DETAIL(), approve: { status: 409, code } });

  const dialog = await openApproveDialog(page, lang);
  await confirmIn(dialog, copy.confirm).click();

  await expect.poll(() => stubs.approvePosts.length).toBe(1);
  await expect(alertIn(dialog)).toContainText(ERRORS[lang][code]);
  await expect(alertIn(dialog)).not.toContainText(PRIVATE_DETAIL);
  await expect(dialog).not.toContainText(PRIVATE_DETAIL);
}

/** A `412`: the page hands the stalemate to the conflict dialog and the status stays where it was. */
async function approveConflictFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].approve;
  const stubs = await openApproval(page, lang, {
    detail: REVIEW_DETAIL(), approve: { status: 412, code: 'story_version_conflict' },
  });

  const dialog = await openApproveDialog(page, lang);
  await confirmIn(dialog, copy.confirm).click();
  await expect.poll(() => stubs.approvePosts.length).toBe(1);

  const conflict = conflictDialog(page, lang);
  await expect(conflict).toBeVisible();
  await expect(conflict).not.toContainText(PRIVATE_DETAIL);
  await expect(statusChip(page, lang, 'story_review')).toBeVisible();

  await conflict.getByRole('button', { name: ERRORS[lang].conflict.cancel, exact: true }).click();
  await expect(conflict).toHaveCount(0);
}

/**
 * A `400 story_attestation_required`: the server's text moved under the user, so the tick is cleared and marked
 * invalid, and the page re-reads the detail before a second confirm can be sent.
 */
async function approveAttestationRequiredFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].approve;
  const stubs = await openApproval(page, lang, {
    detail: TRUE_CRIME_DETAIL(), approve: { status: 400, code: 'story_attestation_required' },
  });
  await page.waitForTimeout(650);
  const readsBefore = stubs.detailGets.length;

  const dialog = await openApproveDialog(page, lang);
  const checkbox = dialog.getByRole('checkbox', { name: copy.attestation.accept, exact: true });
  await checkbox.check();
  await confirmIn(dialog, copy.confirm).click();

  await expect.poll(() => stubs.approvePosts.length).toBe(1);
  await expect(attestationBlock(dialog)).toContainText(ERRORS[lang].story_attestation_required);
  await expect(dialog).not.toContainText(PRIVATE_DETAIL);
  await expect(checkbox).not.toBeChecked();
  await expect(checkbox).toHaveAttribute('aria-invalid', 'true');
  await expect.poll(() => stubs.detailGets.length).toBeGreaterThan(readsBefore);
}

/** An approved episode: reopen sends one POST with the approved version's If-Match and hands the script back. */
async function reopenFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].reopen;
  const stubs = await openApproval(page, lang, {
    detail: APPROVED_DETAIL(), reopen: { status: 200, nextDetail: REOPENED_DETAIL() },
  });
  await recordAnnouncements(page);
  await expect(titleField(page, lang)).not.toBeEditable();

  const launcher = button(page, reopenName(lang), false);
  await expect(launcher).toHaveCount(1);
  await launcher.click();
  const dialog = reopenDialog(page, lang);
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(copy.body);
  await expectNoCostCopy(page.locator('nts-reopen-dialog'));

  await confirmIn(dialog, copy.confirm).click();
  await expect.poll(() => stubs.reopenPosts.length).toBe(1);
  const posted = stubs.reopenPosts[0]!;
  expect(posted.method()).toBe('POST');
  expect(new URL(posted.url()).pathname).toBe(REOPEN_PATH);
  expect(posted.headers()['if-match']).toBe(FRESH_STORY_ETAG);

  await expect(dialog).toHaveCount(0);
  await expect(statusChip(page, lang, 'story_review')).toBeVisible();
  await expect(titleField(page, lang)).toBeEditable();
  await expect.poll(() => announcements(page)).toContain(copy.done);
}

/** A reopen refused because the episode already moved: the fresh state is the answer, so the detail is re-read. */
async function reopenConflictFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].reopen;
  const stubs = await openApproval(page, lang, {
    detail: APPROVED_DETAIL(), reopen: { status: 409, code: 'episode_invalid_state' },
  });
  await page.waitForTimeout(650);
  const readsBefore = stubs.detailGets.length;

  const launcher = button(page, reopenName(lang), false);
  await launcher.click();
  const dialog = reopenDialog(page, lang);
  await expect(dialog).toBeVisible();
  await confirmIn(dialog, copy.confirm).click();

  await expect.poll(() => stubs.reopenPosts.length).toBe(1);
  await expect.poll(() => stubs.detailGets.length).toBeGreaterThan(readsBefore);
  await expect(statusChip(page, lang, 'story_approved')).toBeVisible();
}

/** Quality hints are advisory: they appear next to the dialog and the phase-P dialog never grows a cost line. */
async function hintsAndNoCostFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].approve;
  await openApproval(page, lang, { detail: HINTS_DETAIL() });

  const dialog = await openApproveDialog(page, lang);
  await expect(dialog).toContainText(template(copy.hint, { count: HINTS.length }));
  await expect(confirmIn(dialog, copy.confirm)).toBeEnabled();
  await expectNoCostCopy(page.locator('nts-approve-dialog'));
}

/** Keyboard: focus is trapped inside the dialog, and both Escape and Cancel give it back to the launcher. */
async function focusTrapFlow(page: Page, lang: E2eLang): Promise<void> {
  const copy = COPY[lang].approve;
  await openApproval(page, lang, { detail: REVIEW_DETAIL() });

  const launcher = button(page, approveName(lang), false);
  const dialog = await openApproveDialog(page, lang);
  const surface = page.locator('nts-approve-dialog [role="dialog"]');
  await expect(surface.locator(':focus')).toHaveCount(1);

  for (let press = 1; press <= 8; press += 1) {
    await page.keyboard.press('Tab');
    expect(
      await surface.evaluate((element) => element.contains(document.activeElement)),
      `Tab ${press} left the dialog`,
    ).toBe(true);
  }

  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(launcher).toBeFocused();

  await launcher.click();
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: cancelLabel(lang), exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(launcher).toBeFocused();
}

/** WCAG 2.1 AA on the dialog with the attestation, its hints and its blocked region in reach, in both themes. */
async function axeFlow(page: Page, lang: E2eLang, theme: 'light' | 'dark'): Promise<void> {
  const detail = editorDetail({ currentStory: scriptAt(STORED_VERSION_ID, 1, ATTESTATION, HINTS) });
  await openApproval(page, lang, { detail, theme });

  const dialog = await openApproveDialog(page, lang);
  await expect(attestationBlock(dialog)).toBeVisible();
  await expectA11yClean(page, { include: ['nts-approve-dialog'] });
}

/** At 360 px the confirm action stays reachable and the dialog never forces a horizontal scroll. */
async function narrowDialogFlow(page: Page, lang: E2eLang): Promise<void> {
  await page.setViewportSize({ width: 360, height: 640 });
  await openApproval(page, lang, { detail: TRUE_CRIME_DETAIL() });

  const dialog = await openApproveDialog(page, lang);
  const confirm = confirmIn(dialog, COPY[lang].approve.confirm);
  await confirm.scrollIntoViewIfNeeded();
  await expect(confirm).toBeVisible();
  await expect(confirm).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'no horizontal overflow').toBe(true);
}

test.describe('approve and reopen (vi, stubbed)', () => {
  test.use({ locale: 'vi-VN', timezoneId: 'UTC' });

  test('ApprovePlainEpisode_OnePostWithIfMatchAndNoAttestation_DialogCloses_StatusApprovedAndAnnounced', async ({ page }) => {
    await approvePlainEpisodeFlow(page, 'vi');
  });

  test('ApproveTrueCrime_ConfirmBlockedUntilTick_LiteralMarkup_BodyCarriesTextVersion', async ({ page }) => {
    await approveTrueCrimeFlow(page, 'vi');
  });

  test('Approve409KeywordsFlagged_BlockedRegion_FlagChipOnParagraph3_CommandStaysUnavailable', async ({ page }) => {
    await approveKeywordsFlaggedFlow(page, 'vi');
  });

  test('Approve409FactSheetOutdated_ShowsMappedCopyNeverRawDetail', async ({ page }) => {
    await approveBlockedCodeFlow(page, 'vi', 'story_fact_sheet_outdated');
  });

  test('Approve409StoryModerated_ShowsMappedCopyNeverRawDetail', async ({ page }) => {
    await approveBlockedCodeFlow(page, 'vi', 'story_moderated');
  });

  test('Approve412_OpensConflictDialog_StatusStaysInReview', async ({ page }) => {
    await approveConflictFlow(page, 'vi');
  });

  test('Approve400AttestationRequired_ClearsAndMarksTheTick_RefetchesDetail', async ({ page }) => {
    await approveAttestationRequiredFlow(page, 'vi');
  });

  test('ReopenApprovedEpisode_OnePostWithIfMatch_ChipBackToReview_EditorEditable', async ({ page }) => {
    await reopenFlow(page, 'vi');
  });

  test('Reopen409EpisodeMoved_RefetchesDetail', async ({ page }) => {
    await reopenConflictFlow(page, 'vi');
  });

  test('Dialog_HintsDoNotDisableApprove_AndNoCostCopy', async ({ page }) => {
    await hintsAndNoCostFlow(page, 'vi');
  });

  test('Keyboard_FocusTrappedInDialog_EscapeAndCancelReturnItToTheLauncher', async ({ page }) => {
    await focusTrapFlow(page, 'vi');
  });

  test('Axe_DialogWithAttestationAndHintsCleanInLightTheme', async ({ page }) => {
    await axeFlow(page, 'vi', 'light');
  });

  test('Axe_DialogWithAttestationAndHintsCleanInDarkTheme', async ({ page }) => {
    await axeFlow(page, 'vi', 'dark');
  });

  test('Narrow360_ConfirmActionStaysReachable', async ({ page }) => {
    await narrowDialogFlow(page, 'vi');
  });
});

test.describe('approve and reopen (en, stubbed)', () => {
  test.use({ locale: 'en-US', timezoneId: 'UTC' });

  test('ApprovePlainEpisode_OnePostWithIfMatchAndNoAttestation_DialogCloses_StatusApprovedAndAnnounced', async ({ page }) => {
    await approvePlainEpisodeFlow(page, 'en');
  });

  test('ApproveTrueCrime_ConfirmBlockedUntilTick_LiteralMarkup_BodyCarriesTextVersion', async ({ page }) => {
    await approveTrueCrimeFlow(page, 'en');
  });

  test('Approve409KeywordsFlagged_BlockedRegion_FlagChipOnParagraph3_CommandStaysUnavailable', async ({ page }) => {
    await approveKeywordsFlaggedFlow(page, 'en');
  });

  test('Approve409FactSheetOutdated_ShowsMappedCopyNeverRawDetail', async ({ page }) => {
    await approveBlockedCodeFlow(page, 'en', 'story_fact_sheet_outdated');
  });

  test('Approve409StoryModerated_ShowsMappedCopyNeverRawDetail', async ({ page }) => {
    await approveBlockedCodeFlow(page, 'en', 'story_moderated');
  });

  test('Approve412_OpensConflictDialog_StatusStaysInReview', async ({ page }) => {
    await approveConflictFlow(page, 'en');
  });

  test('Approve400AttestationRequired_ClearsAndMarksTheTick_RefetchesDetail', async ({ page }) => {
    await approveAttestationRequiredFlow(page, 'en');
  });

  test('ReopenApprovedEpisode_OnePostWithIfMatch_ChipBackToReview_EditorEditable', async ({ page }) => {
    await reopenFlow(page, 'en');
  });

  test('Reopen409EpisodeMoved_RefetchesDetail', async ({ page }) => {
    await reopenConflictFlow(page, 'en');
  });

  test('Dialog_HintsDoNotDisableApprove_AndNoCostCopy', async ({ page }) => {
    await hintsAndNoCostFlow(page, 'en');
  });

  test('Keyboard_FocusTrappedInDialog_EscapeAndCancelReturnItToTheLauncher', async ({ page }) => {
    await focusTrapFlow(page, 'en');
  });

  test('Axe_DialogWithAttestationAndHintsCleanInLightTheme', async ({ page }) => {
    await axeFlow(page, 'en', 'light');
  });

  test('Axe_DialogWithAttestationAndHintsCleanInDarkTheme', async ({ page }) => {
    await axeFlow(page, 'en', 'dark');
  });

  test('Narrow360_ConfirmActionStaysReachable', async ({ page }) => {
    await narrowDialogFlow(page, 'en');
  });
});
