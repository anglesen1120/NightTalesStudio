import type { AppError, Result } from '@core/errors';
import { BibleRepository, type BibleWriteResult } from '../application/bible.repository';
import type { Bible, BibleContent, BibleProposal } from '../domain/bible.model';

export const BIBLE_SERIES_ID = '11111111-1111-4111-8111-111111111111';
export const OTHER_SERIES_ID = '99999999-9999-4999-8999-999999999999';
export const PROPOSING_EPISODE_ID = '22222222-2222-4222-8222-222222222222';
export const OTHER_EPISODE_ID = '33333333-3333-4333-8333-333333333333';

/** One scripted answer per call; a call with no script left answers the seeded result. */
type Answer<T> = () => Promise<T>;

/** A complete, bilingual-safe content fixture: one of every section so a mapper or store spec needs no partials. */
export function bibleContent(overrides: Partial<BibleContent> = {}): BibleContent {
  return {
    narrator: { name: 'Người kể chuyện', persona: 'Giọng trầm, chậm', voiceNotes: 'Ngắt câu dài' },
    recurringCharacters: [
      { id: 'ch1', name: 'Bà Hoa', description: 'Người gác cổng khu chung cư', firstEpisodeId: 'ep-1' },
    ],
    places: [{ id: 'pl1', name: 'Chung cư cũ', description: 'Hành lang tối, đèn nhấp nháy' }],
    rules: [{ id: 'rl1', text: 'Không ai được mở cửa sau 23 giờ' }],
    episodeSummaries: [
      {
        episodeId: 'ep-1',
        title: 'Tập 1 — Tiếng gõ lúc nửa đêm',
        summary: 'Bà Hoa nghe tiếng gõ cửa nhưng hành lang trống.',
        structure: { sub_genre: 'ghost', opening: 'hook', twist_type: 'reveal', setting: 'apartment', narrator_mode: 'first', beats: [] },
        approvedAt: '2026-10-01T10:00:00Z',
      },
    ],
    ...overrides,
  };
}

/** A bible fixture with a pending proposal: one op of every value-bearing kind plus a reason. */
export function bibleProposal(overrides: Partial<BibleProposal> = {}): BibleProposal {
  return {
    basedOnVersion: 1,
    proposedFromEpisodeId: PROPOSING_EPISODE_ID,
    proposedAt: '2026-10-02T10:00:00Z',
    ops: [
      { kind: 'AddCharacter', name: 'Ông Tư', description: 'Hàng xóm tầng dưới', reason: 'Nhân vật mới xuất hiện' },
      { kind: 'AddRule', text: 'Không nhắc tên người đã khuất', reason: 'Luật mới' },
      { kind: 'AddSummary', summary: 'Tập 2 tóm tắt', reason: 'Tập vừa duyệt' },
    ],
    preview: { characters: 1, places: 0, rules: 1, summaryForEpisodeId: 'ep-2' },
    ...overrides,
  };
}

/** A loaded bible; `overrides` replaces whole members (`content`, `proposal`, version, etag, series). */
export function bible(overrides: Partial<Bible> = {}): Bible {
  return {
    seriesId: BIBLE_SERIES_ID,
    version: 1,
    etag: '"sb-1"',
    content: bibleContent(),
    proposal: null,
    ...overrides,
  };
}

/** The three failures a store spec scripts most often, spelled like the mapper's own answers. */
export const BIBLE_NOT_FOUND: AppError = { code: 'not_found', status: 404, kind: 'not_found', fieldErrors: {} };
export const BIBLE_CONFLICT: AppError = { code: 'bible_conflict', status: 412, kind: 'conflict', fieldErrors: {} };
export const BIBLE_PROPOSAL_GONE: AppError = {
  code: 'bible_proposal_not_pending',
  status: 409,
  kind: 'precondition',
  fieldErrors: {},
};
export const BIBLE_INVALID: AppError = {
  code: 'bible_invalid_content',
  status: 400,
  kind: 'validation',
  fieldErrors: { recurring_characters: ['too_many'], 'episode_summaries[2].episode_id': ['not_approved_episode'] },
};
export const NETWORK_ERROR: AppError = { code: 'network_error', status: 0, kind: 'network', fieldErrors: {} };

/**
 * Hand-written scriptable `BibleRepository` (README D15: no mocking library). Every call records its arguments and
 * answers the next scripted reply, falling back to the seeded result — a spec scripts only the answers it cares
 * about and asserts the rest (a refetch, a title count) through the call log.
 */
export class FakeBibleRepository extends BibleRepository {
  readonly getCalls: string[] = [];
  readonly saveCalls: Array<{ seriesId: string; content: BibleContent; etag: string }> = [];
  readonly acceptCalls: string[] = [];
  readonly dismissCalls: string[] = [];
  readonly titleCalls: string[] = [];

  getResult: Result<Bible> = { ok: true, value: bible() };
  saveResult: Result<BibleWriteResult> = { ok: true, value: { version: 2, etag: '"sb-2"' } };
  acceptResult: Result<void> = { ok: true, value: undefined };
  dismissResult: Result<void> = { ok: true, value: undefined };
  titleResult: string | null = 'Tập đã duyệt';

  private readonly getScript: Array<Answer<Result<Bible>>> = [];
  private readonly saveScript: Array<Answer<Result<BibleWriteResult>>> = [];
  private readonly acceptScript: Array<Answer<Result<void>>> = [];
  private readonly dismissScript: Array<Answer<Result<void>>> = [];
  private readonly titleScript: Array<Answer<string | null>> = [];

  scriptGet(answer: Answer<Result<Bible>>): this {
    this.getScript.push(answer);
    return this;
  }

  scriptSave(answer: Answer<Result<BibleWriteResult>>): this {
    this.saveScript.push(answer);
    return this;
  }

  scriptAccept(answer: Answer<Result<void>>): this {
    this.acceptScript.push(answer);
    return this;
  }

  scriptDismiss(answer: Answer<Result<void>>): this {
    this.dismissScript.push(answer);
    return this;
  }

  scriptTitle(answer: Answer<string | null>): this {
    this.titleScript.push(answer);
    return this;
  }

  override get(seriesId: string): Promise<Result<Bible>> {
    this.getCalls.push(seriesId);
    return this.getScript.shift()?.() ?? Promise.resolve(this.getResult);
  }

  override save(seriesId: string, content: BibleContent, etag: string): Promise<Result<BibleWriteResult>> {
    this.saveCalls.push({ seriesId, content, etag });
    return this.saveScript.shift()?.() ?? Promise.resolve(this.saveResult);
  }

  override accept(seriesId: string): Promise<Result<void>> {
    this.acceptCalls.push(seriesId);
    return this.acceptScript.shift()?.() ?? Promise.resolve(this.acceptResult);
  }

  override dismiss(seriesId: string): Promise<Result<void>> {
    this.dismissCalls.push(seriesId);
    return this.dismissScript.shift()?.() ?? Promise.resolve(this.dismissResult);
  }

  override getEpisodeTitle(episodeId: string): Promise<string | null> {
    this.titleCalls.push(episodeId);
    return this.titleScript.shift()?.() ?? Promise.resolve(this.titleResult);
  }
}
