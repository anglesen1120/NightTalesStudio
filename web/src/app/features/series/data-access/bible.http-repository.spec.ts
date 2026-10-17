import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { BibleContent } from '../domain/bible.model';
import { BIBLE_SERIES_ID, PROPOSING_EPISODE_ID } from '../testing/fake-bible.repository';
import { BibleHttpRepository } from './bible.http-repository';

const BIBLE_URL = `/api/v1/series/${BIBLE_SERIES_ID}/bible`;
const EPISODE_URL = `/api/v1/episodes/${PROPOSING_EPISODE_ID}`;

const WIRE_BODY = {
  seriesId: BIBLE_SERIES_ID,
  version: 3,
  etag: '"sb-3"',
  proposal: null,
  content: {
    schema_version: 'bible.v1',
    narrator: { name: 'Người kể', persona: 'Giọng trầm', voice_notes: 'Ngắt câu dài' },
    recurring_characters: [{ id: 'ch1', name: 'Bà Hoa', description: 'Gác cổng', first_episode_id: 'ep-1' }],
    places: [],
    rules: [{ id: 'rl1', text: 'Không mở cửa sau 23 giờ' }],
    episode_summaries: [{
      episode_id: 'ep-1',
      title: 'Tập 1',
      summary: 'Tóm tắt',
      structure: { sub_genre: 'ghost', opening: 'hook', twist_type: 'reveal', setting: 'apartment', narrator_mode: 'first', beats: [] },
      approved_at: '2026-10-01T10:00:00Z',
    }],
  },
};

const CONTENT: BibleContent = {
  narrator: { name: 'Người kể', persona: 'Giọng trầm', voiceNotes: 'Ngắt câu dài' },
  recurringCharacters: [
    { id: 'ch1', name: 'Bà Hoa', description: 'Gác cổng', firstEpisodeId: 'ep-1' },
    { name: 'Ông Tư', description: 'Hàng xóm' },
  ],
  places: [],
  rules: [{ text: 'Luật mới' }],
  episodeSummaries: [
    {
      episodeId: 'ep-1',
      title: 'Tập 1',
      summary: 'Tóm tắt',
      structure: { sub_genre: 'ghost' },
      approvedAt: '2026-10-01T10:00:00Z',
    },
  ],
};

let http: HttpTestingController;
let repository: BibleHttpRepository;

beforeEach(() => {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      BibleHttpRepository,
      { provide: APP_ERROR_DEV_MODE, useValue: false },
    ],
  });
  http = TestBed.inject(HttpTestingController);
  repository = TestBed.inject(BibleHttpRepository);
});

afterEach(() => http.verify());

describe('BibleHttpRepository', () => {
  it('reads the bible once and maps the wire body with its quoted ETag', async () => {
    const pending = repository.get(BIBLE_SERIES_ID);
    const request = http.expectOne(BIBLE_URL);
    expect(request.request.method).toBe('GET');
    request.flush(WIRE_BODY, { headers: { ETag: '"sb-3"' } });

    const result = await pending;
    if (!result.ok) throw new Error(`unexpected failure ${result.error.code}`);
    expect(result.value.version).toBe(3);
    expect(result.value.etag).toBe('"sb-3"');
    expect(result.value.content.narrator.voiceNotes).toBe('Ngắt câu dài');
  });

  it('saves the exact snake_case content with If-Match and no schema_version', async () => {
    const pending = repository.save(BIBLE_SERIES_ID, CONTENT, '"sb-1"');
    const request = http.expectOne(BIBLE_URL);
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('If-Match')).toBe('"sb-1"');
    expect(request.request.body).toEqual({
      content: {
        narrator: { name: 'Người kể', persona: 'Giọng trầm', voice_notes: 'Ngắt câu dài' },
        recurring_characters: [
          { id: 'ch1', name: 'Bà Hoa', description: 'Gác cổng', first_episode_id: 'ep-1' },
          { name: 'Ông Tư', description: 'Hàng xóm', first_episode_id: null },
        ],
        places: [],
        rules: [{ text: 'Luật mới' }],
        episode_summaries: [{ episode_id: 'ep-1', title: 'Tập 1', summary: 'Tóm tắt' }],
      },
    });
    const serialized = JSON.stringify(request.request.body);
    expect(serialized).not.toContain('schema_version');
    expect(serialized).not.toContain('approved_at');
    expect(serialized).not.toContain('structure');
    const body = request.request.body as { content: { recurring_characters: Array<Record<string, unknown>> } };
    expect('id' in body.content.recurring_characters[0]!).toBe(true);
    expect('id' in body.content.recurring_characters[1]!).toBe(false);

    request.flush({ version: 2, etag: '"sb-2"' }, { headers: { ETag: '"sb-2"' } });
    const result = await pending;
    if (!result.ok) throw new Error(`unexpected failure ${result.error.code}`);
    expect(result.value).toEqual({ version: 2, etag: '"sb-2"' });
  });

  it('maps a stale save to bible_conflict', async () => {
    const pending = repository.save(BIBLE_SERIES_ID, CONTENT, '"sb-1"');
    http.expectOne(BIBLE_URL).flush(
      { code: 'bible_conflict', errors: {}, traceId: 'trace-1' },
      { status: 412, statusText: 'Precondition Failed' },
    );

    const result = await pending;
    if (result.ok) throw new Error('expected the stale write to fail');
    expect(result.error).toMatchObject({ code: 'bible_conflict', status: 412, kind: 'conflict' });
  });

  it('keeps the server field-error keys of bible_invalid_content verbatim', async () => {
    const pending = repository.save(BIBLE_SERIES_ID, CONTENT, '"sb-1"');
    http.expectOne(BIBLE_URL).flush(
      {
        code: 'bible_invalid_content',
        errors: { recurring_characters: ['too_many'], 'episode_summaries[2].episode_id': ['not_approved_episode'] },
        traceId: 'trace-2',
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const result = await pending;
    if (result.ok) throw new Error('expected the invalid save to fail');
    expect(result.error.kind).toBe('validation');
    expect(result.error.fieldErrors).toEqual({
      recurring_characters: ['too_many'],
      'episode_summaries[2].episode_id': ['not_approved_episode'],
    });
  });

  it('accepts the proposal without a body or an If-Match', async () => {
    const pending = repository.accept(BIBLE_SERIES_ID);
    const request = http.expectOne(`${BIBLE_URL}/proposal/accept`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.has('If-Match')).toBe(false);
    expect(request.request.body).toBeNull();
    request.flush({ version: 2, etag: '"sb-2"' });

    expect(await pending).toEqual({ ok: true, value: undefined });
  });

  it('dismisses the proposal without a body or an If-Match', async () => {
    const pending = repository.dismiss(BIBLE_SERIES_ID);
    const request = http.expectOne(`${BIBLE_URL}/proposal/dismiss`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.has('If-Match')).toBe(false);
    expect(request.request.body).toBeNull();
    request.flush(null, { status: 204, statusText: 'No Content' });

    expect(await pending).toEqual({ ok: true, value: undefined });
  });

  it('reads only the title of the proposing episode, and null on a failure', async () => {
    const title = repository.getEpisodeTitle(PROPOSING_EPISODE_ID);
    const request = http.expectOne(EPISODE_URL);
    expect(request.request.method).toBe('GET');
    request.flush({ id: PROPOSING_EPISODE_ID, title: 'Tập 1 — Tiếng gõ' });
    expect(await title).toBe('Tập 1 — Tiếng gõ');

    const missing = repository.getEpisodeTitle(PROPOSING_EPISODE_ID);
    http.expectOne(EPISODE_URL).flush(
      { code: 'not_found', errors: {}, traceId: 'trace-3' },
      { status: 404, statusText: 'Not Found' },
    );
    expect(await missing).toBeNull();
  });

  it('reports a missing bible as not_found', async () => {
    const pending = repository.get(BIBLE_SERIES_ID);
    http.expectOne(BIBLE_URL).flush(
      { code: 'not_found', errors: {}, traceId: 'trace-4' },
      { status: 404, statusText: 'Not Found' },
    );

    const result = await pending;
    if (result.ok) throw new Error('expected the read to fail');
    expect(result.error).toMatchObject({ code: 'not_found', status: 404, kind: 'not_found' });
  });
});
