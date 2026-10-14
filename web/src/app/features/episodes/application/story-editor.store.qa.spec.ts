import { signal } from '@angular/core';
import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { BehaviorSubject, of } from 'rxjs';
import { vi } from 'vitest';
import type { AppError } from '@core/errors';
import { APP_ERROR_DEV_MODE } from '@core/errors/app-error-mapper';
import { RealtimeService } from '@core/realtime';
import { SessionStore } from '@core/session';
import { ConfirmDialog, ConflictDialog } from '@shared/ui';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import coreVi from '../../../../assets/i18n/core/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import type { EpisodeDetail } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import type { StoryDraftWire, StorySnapshot } from '../domain/story.model';
import { StoryRepository } from '../domain/story.repository';
import { StoryVersionRepository } from '../domain/story-version.repository';
import { StoryReviewPage } from '../feature/story-review.page';
import { FakeEpisodeRealtime } from '../testing/fake-episode-realtime';
import { EPISODE_ID, FakeEpisodesRepository, episodeDetail } from '../testing/fake-episodes.repository';
import { FakeStoryRepository, storyContent, storySnapshot } from '../testing/fake-story.repository';
import { FakeStoryVersionRepository } from '../testing/fake-story-version.repository';
import { EpisodeDetailStore } from './episode-detail.store';
import { StoryEditorStore } from './story-editor.store';

// QA S26 (10.5.3-T1): negative/boundary matrix beyond story-editor.store.spec.ts. Deterministic, fake timers only where
// the 150 ms dirty debounce / 250 ms invalidation coalescing is under test.

function appError(code: string, status: number, extra: Partial<AppError> = {}): AppError {
  return { code, status, kind: status === 400 ? 'validation' : status === 412 ? 'conflict' : 'precondition', fieldErrors: {}, ...extra };
}
function rejected<T>(error: AppError): Promise<T> {
  // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- structured domain-port failure
  return Promise.reject(error);
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}
async function settle(): Promise<void> { for (let i = 0; i < 12; i++) await Promise.resolve(); }

interface Harness {
  readonly episodes: FakeEpisodesRepository; readonly stories: FakeStoryRepository;
  readonly realtime: FakeEpisodeRealtime; readonly sessionGeneration: WritableSignal<number>;
}
function harness(snapshot: StorySnapshot | null, actions: readonly string[], status = 'story_review'): Harness {
  const episodes = new FakeEpisodesRepository();
  episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: snapshot, hasCurrentStory: snapshot !== null, nextActions: actions, status })));
  return { episodes, stories: new FakeStoryRepository(), realtime: new FakeEpisodeRealtime(), sessionGeneration: signal(0) };
}
function providers(h: Harness) {
  return [
    ...ZONELESS_TEST_PROVIDERS,
    { provide: EpisodesRepository, useValue: h.episodes }, { provide: StoryRepository, useValue: h.stories },
    // The page provides the real VersionsStore (it clears the panel's needs-reopen notice), so its own repository
    // port needs a fake here even though these specs never open the panel.
    { provide: StoryVersionRepository, useValue: new FakeStoryVersionRepository() },
    { provide: RealtimeService, useValue: h.realtime }, { provide: SessionStore, useValue: { generation: h.sessionGeneration } },
    { provide: APP_ERROR_DEV_MODE, useValue: false },
  ];
}
async function loaded(snapshot: StorySnapshot | null = storySnapshot(), actions: readonly string[] = ['edit_story']): Promise<Loaded> {
  const h = harness(snapshot, actions);
  TestBed.configureTestingModule({ providers: [...providers(h), EpisodeDetailStore, StoryEditorStore] });
  const store = TestBed.inject(StoryEditorStore), detail = TestBed.inject(EpisodeDetailStore);
  await store.load(EPISODE_ID);
  return { ...h, store, detail };
}
interface Loaded extends Harness { readonly store: StoryEditorStore; readonly detail: EpisodeDetailStore }
function nextDetail(h: Loaded, overrides: Partial<EpisodeDetail>): void {
  h.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], status: 'story_review', ...overrides })));
}
function failWith(h: Loaded, error: AppError): void { h.stories.scriptSave(() => rejected(error)); }

describe('StoryEditorStore QA', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { TestBed.resetTestingModule(); vi.useRealTimers(); });

  describe('400 story_invalid_content pointer mapping', () => {
    const SECRET = 'RAW-SERVER-DETAIL-SECRET';
    it.each([
      ['hook.text', 'hook'], ['hook.keywords[1]', 'hook'], ['ending.fact_refs[0]', 'ending'], ['ending', 'ending'],
      ['description', 'meta'], ['tags', 'meta'], ['content_warnings', 'meta'], ['paragraphs[2].beat', 'p:2'], ['p:1', 'p:1'], ['meta', 'meta'],
    ])('maps %s to the %s segment and never leaks the server message', async (pointer, address) => {
      const h = await loaded(); h.store.setSegmentText('title', 'edited');
      failWith(h, appError('story_invalid_content', 400, { fieldErrors: { [pointer]: [SECRET] } }));
      await h.store.saveVersion();
      expect(Object.keys(h.store.fieldErrors())).toEqual([address]);
      expect(h.store.formErrors()).toEqual([]);
      expect(JSON.stringify([h.store.fieldErrors(), h.store.formErrors()])).not.toContain(SECRET);
      expect(h.store.hasUnsavedChanges()).toBe(true);
    });

    it.each([
      'p:99', 'p:03', 'p:-1', 'paragraphs[01].text', 'paragraphs[99].text', 'structure.sub_genre', 'title', 'unknown', '', 'p:1\n', 'hook.text\u0000',
      'p:\uD800', 'paragraphs[9007199254740993].text', 'HOOK', ' hook', 'hook.', 'constructor', 'toString',
    ])('routes the unmappable/out-of-range pointer %j to the form-level alert without throwing', async (pointer) => {
      const h = await loaded(); h.store.setSegmentText('title', 'edited');
      failWith(h, appError('story_invalid_content', 400, { fieldErrors: { [pointer]: [SECRET] } }));
      await expect(h.store.saveVersion()).resolves.toBeUndefined();
      // `title` is a canonical address too (parseAddress accepts it) and is the one pointer allowed to land on a segment.
      if (pointer === 'title') expect(Object.keys(h.store.fieldErrors())).toEqual(['title']);
      else { expect(h.store.fieldErrors()).toEqual({}); expect(h.store.formErrors()).toEqual(['errors.story_invalid_content']); }
      expect(JSON.stringify([h.store.fieldErrors(), h.store.formErrors()])).not.toContain(SECRET);
    });

    it('survives a __proto__ field key without polluting Object.prototype or throwing', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'edited');
      const fieldErrors = JSON.parse(`{"__proto__":["${SECRET}"],"p:0":["${SECRET}"]}`) as Record<string, string[]>;
      failWith(h, appError('story_invalid_content', 400, { fieldErrors }));
      await h.store.saveVersion();
      expect(({} as Record<string, unknown>)['polluted']).toBeUndefined();
      expect(Object.keys(h.store.fieldErrors())).toEqual(['p:0']);
      expect(h.store.formErrors()).toEqual(['errors.story_invalid_content']);
    });

    it('mixes mapped and unmapped keys: the segment keeps its error, only the unmapped one reaches the alert', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'edited');
      failWith(h, appError('story_invalid_content', 400, { fieldErrors: { 'p:2': [SECRET], mystery: [SECRET] } }));
      await h.store.saveVersion();
      expect(h.store.fieldErrors()['p:2']).toEqual(['errors.story_invalid_content']);
      expect(h.store.formErrors()).toEqual(['errors.story_invalid_content']);
    });

    it('a 400 with no field errors and no rows shows the generic mapped message once', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'edited');
      failWith(h, appError('story_invalid_content', 400, { detail: SECRET } as Partial<AppError>));
      await h.store.saveVersion();
      expect(h.store.fieldErrors()).toEqual({}); expect(h.store.formErrors()).toEqual(['errors.story_invalid_content']);
      expect(JSON.stringify(h.store.error())).not.toContain('never'); // the AppError may carry detail, the view model never reads it
    });

    it('does not map field errors for other 400 codes onto segments', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'edited');
      failWith(h, appError('validation_failed', 400, { fieldErrors: { 'p:0': [SECRET] } }));
      await h.store.saveVersion();
      expect(h.store.fieldErrors()).toEqual({}); expect(h.store.formErrors()).toHaveLength(1);
      expect(h.store.formErrors()[0]).not.toContain(SECRET);
    });

    it('typing after a 400 clears the stale segment errors and a successful save clears them too', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'edited');
      failWith(h, appError('story_invalid_content', 400, { fieldErrors: { 'p:0': ['x'], mystery: ['y'] } }));
      await h.store.saveVersion(); expect(h.store.fieldErrors()['p:0']).toBeDefined();
      h.store.setText(h.store.paragraphs()[0]!.segKey, 'fixed'); expect(h.store.fieldErrors()).toEqual({}); expect(h.store.formErrors()).toEqual([]);
      failWith(h, appError('story_invalid_content', 400, { fieldErrors: { 'p:1': ['x'] } }));
      await h.store.saveVersion(); expect(h.store.fieldErrors()['p:1']).toBeDefined();
      h.store.setText(h.store.paragraphs()[1]!.segKey, 'fixed too');
      await h.store.saveVersion(); expect(h.store.error()).toBeNull(); expect(h.store.fieldErrors()).toEqual({});
    });
  });

  describe('save timing and concurrency', () => {
    it('saves the newest text immediately after an edit (t=0, inside the 150 ms debounce) and stays clean afterwards', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'typed just now');
      expect(h.store.saveDisabled()).toBe(false); expect(h.store.isDirty()).toBe(false);
      await h.store.saveVersion();
      expect(h.stories.calls).toHaveLength(1); expect(h.stories.calls[0]?.content.title).toBe('typed just now');
      vi.advanceTimersByTime(1000);
      expect(h.store.isDirty()).toBe(false); expect(h.store.hasUnsavedChanges()).toBe(false); expect(h.store.saveDisabled()).toBe(true);
    });

    it('enables Save synchronously (well under 150 ms) on the first keystroke and disables it again when the text is reverted', async () => {
      const h = await loaded(); expect(h.store.saveDisabled()).toBe(true);
      h.store.setSegmentText('title', 'x'); expect(h.store.saveDisabled()).toBe(false);
      h.store.setSegmentText('title', 'The lantern'); expect(h.store.saveDisabled()).toBe(true);
      vi.advanceTimersByTime(150); expect(h.store.isDirty()).toBe(false);
    });

    it('three concurrent saveVersion() calls issue exactly one PUT', async () => {
      const h = await loaded(); const response = deferred<StorySnapshot>(); h.stories.scriptSave(() => response.promise);
      h.store.setSegmentText('title', 'a');
      const all = Promise.all([h.store.saveVersion(), h.store.saveVersion(), h.store.saveVersion()]);
      expect(h.stories.calls).toHaveLength(1);
      response.resolve(storySnapshot({ etag: '"sv-2"' })); await all; expect(h.stories.calls).toHaveLength(1);
    });

    it('an edit typed during the PUT that is reverted to the sent text before the 200 leaves nothing dirty', async () => {
      const h = await loaded(); const response = deferred<StorySnapshot>(); h.stories.scriptSave(() => response.promise);
      h.store.setSegmentText('title', 'sent'); const save = h.store.saveVersion();
      h.store.setSegmentText('title', 'typed meanwhile'); h.store.setSegmentText('title', 'sent');
      response.resolve(storySnapshot({ etag: '"sv-2"' })); await save; vi.advanceTimersByTime(150);
      expect(h.store.hasUnsavedChanges()).toBe(false); expect(h.store.isDirty()).toBe(false); expect(h.store.saveDisabled()).toBe(true);
    });

    it('two rapid sequential saves chain the returned ETag and each sends the newest text', async () => {
      const h = await loaded();
      h.stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-2"', version: 2 }))); h.stories.scriptSave(() => Promise.resolve(storySnapshot({ etag: '"sv-3"', version: 3 })));
      h.store.setSegmentText('title', 'one'); await h.store.saveVersion(); h.store.setSegmentText('title', 'two'); await h.store.saveVersion();
      expect(h.stories.calls.map((call) => [call.etag, call.content.title])).toEqual([[storySnapshot().etag, 'one'], ['"sv-2"', 'two']]);
      expect(h.store.snapshot()?.version).toBe(3);
    });

    it('a failed PUT releases the saving latch so the next explicit save is possible and sends the same body', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'again');
      failWith(h, appError('network_error', 0, { kind: 'network' }));
      await h.store.saveVersion(); expect(h.store.saving()).toBe(false); expect(h.store.hasUnsavedChanges()).toBe(true);
      await h.store.saveVersion(); expect(h.stories.calls).toHaveLength(2);
      expect(h.stories.calls[1]?.content).toEqual(h.stories.calls[0]?.content); expect(h.stories.calls[1]?.etag).toBe(h.stories.calls[0]?.etag);
    });
  });

  describe('409 keywords_flagged boundaries', () => {
    it('a refusal with no usable row points at no chip: the flags already on screen stay and the copy is the error copy', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'mine');
      expect(h.store.setKeywordFlags([{ address: 'p:0', index: 0, rule: 'no_scenes' }])).toBe(true);
      failWith(h, appError('keywords_flagged', 409, { problem: { errors: [{ segment: 7, index: 1, rule: 'keyword_invalid' }] } }));
      await h.store.saveVersion();
      // `applyKeywordsFlaggedProblem` reports false for a row set it cannot use, so the caller keeps its own error copy
      // and nothing on screen is replaced by an empty flag set.
      expect(h.store.keywordFlags()).toEqual([{ address: 'p:0', index: 0, rule: 'no_scenes' }]);
      expect(h.store.formErrors()).toEqual(['errors.keywords_flagged']);
      expect(h.store.hasUnsavedChanges()).toBe(true); expect(h.store.saving()).toBe(false);
    });

    it('a flag list for another episode is refused and leaves the current chips alone', async () => {
      const h = await loaded();
      expect(h.store.setKeywordFlags([{ address: 'p:1', index: 1, rule: 'keyword_name_detected' }], 'episode-b')).toBe(false);
      expect(h.store.keywordFlags()).toEqual([]); expect(h.store.videoBlocked()).toBe(false);
      expect(h.store.setKeywordFlags([{ address: 'p:1', index: 1, rule: 'keyword_name_detected' }], EPISODE_ID)).toBe(true);
      expect(h.store.keywordFlags()).toHaveLength(1); expect(h.store.videoBlocked()).toBe(true);
    });
  });

  describe('412 conflict boundaries', () => {
    it('a second plain save after Cancel gets 412 again: no automatic retry, draft and ETag untouched', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'mine');
      // The store reads the server story once per 412 (user-authorised diff), and that read keeps the episode editable.
      nextDetail(h, { currentStory: storySnapshot({ etag: '"sv-theirs"' }) });
      failWith(h, appError('story_version_conflict', 412)); await h.store.saveVersion();
      await h.store.resolveConflict('cancel');
      nextDetail(h, { currentStory: storySnapshot({ etag: '"sv-theirs"' }) });
      failWith(h, appError('story_version_conflict', 412)); await h.store.saveVersion(); vi.advanceTimersByTime(5000); await settle();
      expect(h.stories.calls).toHaveLength(2); expect(h.stories.calls.every((call) => call.etag === storySnapshot().etag)).toBe(true);
      // Two reads and no more: the load plus one user-authorised diff read per 412 — never a retry with a fresh ETag.
      expect(h.episodes.getCalls).toHaveLength(3); expect(h.episodes.getCalls.slice(1).every((call) => call.userAction)).toBe(true);
      expect(h.store.conflict()).not.toBeNull(); expect(h.store.title()).toBe('mine'); expect(h.store.snapshot()?.etag).toBe(storySnapshot().etag);
    });

    it('text typed while the conflict dialog is open is what "Save mine as new" sends', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'before');
      // The 412 diff read answers with an editable server copy, so the draft stays editable while the dialog is open.
      nextDetail(h, { currentStory: storySnapshot({ etag: '"sv-theirs"' }) });
      failWith(h, appError('story_version_conflict', 412)); await h.store.saveVersion();
      h.store.setSegmentText('title', 'typed behind the dialog'); nextDetail(h, { currentStory: storySnapshot({ etag: '"sv-fresh"' }) });
      await h.store.resolveConflict('saveAsNew');
      expect(h.stories.calls[1]).toMatchObject({ etag: '"sv-fresh"' }); expect(h.stories.calls[1]?.content.title).toBe('typed behind the dialog');
    });

    it('Save-as-new whose re-read fails sends no PUT, keeps the draft and clears the stale conflict', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'mine');
      failWith(h, appError('story_version_conflict', 412)); await h.store.saveVersion();
      h.episodes.scriptGet(() => rejected(appError('network_error', 0, { kind: 'network' })));
      await h.store.resolveConflict('saveAsNew');
      expect(h.stories.calls).toHaveLength(1); expect(h.store.title()).toBe('mine'); expect(h.store.conflict()).toBeNull(); expect(h.store.error()?.status).toBe(0);
      expect(h.store.saving()).toBe(false);
      await h.store.resolveConflict('cancel'); expect(h.store.conflict()).toBeNull();
    });

    it('Save-as-new that discovers the episode is no longer editable sends no PUT and refuses further edits', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'mine');
      failWith(h, appError('story_version_conflict', 412)); await h.store.saveVersion();
      nextDetail(h, { nextActions: ['reopen_story'], status: 'story_approved', currentStory: storySnapshot({ etag: '"sv-fresh"' }) });
      await h.store.resolveConflict('saveAsNew');
      expect(h.stories.calls).toHaveLength(1); expect(h.store.invalidState()).toBe(true); expect(h.store.readOnly()).toBe(true);
      expect(h.store.title()).toBe('mine');
      h.store.setSegmentText('title', 'forbidden'); expect(h.store.title()).toBe('mine');
    });

    it('Save-as-new is abandoned after logout: the late re-read never produces a PUT', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'mine');
      failWith(h, appError('story_version_conflict', 412)); await h.store.saveVersion();
      const read = deferred<EpisodeDetail>(); h.episodes.scriptGet(() => read.promise);
      const pending = h.store.resolveConflict('saveAsNew');
      h.sessionGeneration.update((value) => value + 1); TestBed.tick();
      read.resolve(episodeDetail({ currentStory: storySnapshot({ etag: '"sv-fresh"' }), nextActions: ['edit_story'] })); await pending; await settle();
      expect(h.stories.calls).toHaveLength(1); expect(h.store.snapshot()).toBeNull();
    });

    it('the 412 handler issues one user-authorised diff read and never a second PUT without a user decision', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'mine');
      failWith(h, appError('story_version_conflict', 412)); await h.store.saveVersion(); vi.advanceTimersByTime(10_000); await settle();
      expect(h.stories.calls).toHaveLength(1);
      expect(h.episodes.getCalls).toHaveLength(2); expect(h.episodes.getCalls[1]).toEqual({ episodeId: EPISODE_ID, userAction: true });
      expect(h.store.conflict()).not.toBeNull(); expect(h.store.saving()).toBe(false); expect(h.store.conflictDiff()).toEqual([]);
      // That one read answered without a server story, so the empty list is an unknown diff, never "nothing differs".
      expect(h.store.conflictDiffKnown()).toBe(false);
    });
  });

  describe('late results and session boundaries', () => {
    it('a late PUT 200 for episode A cannot touch the draft/baseline of episode B opened meanwhile', async () => {
      const h = await loaded(); const late = deferred<StorySnapshot>(); h.stories.scriptSave(() => late.promise);
      h.store.setSegmentText('title', 'A edit'); const save = h.store.saveVersion();
      nextDetail(h, { id: 'episode-b', currentStory: storySnapshot({ etag: '"sv-b"', content: storyContent({ title: 'B title' }) }) });
      await h.store.load('episode-b');
      late.resolve(storySnapshot({ etag: '"sv-a-late"', version: 9 })); await save; await settle();
      expect(h.store.title()).toBe('B title'); expect(h.store.snapshot()?.etag).toBe('"sv-b"'); expect(h.store.hasUnsavedChanges()).toBe(false);
      expect(h.store.saving()).toBe(false); expect(h.store.savedAt()).toBeNull();
    });

    it('a late PUT failure for the previous episode cannot raise errors or a conflict on the new one', async () => {
      const h = await loaded(); const late = deferred<StorySnapshot>(); h.stories.scriptSave(() => late.promise);
      h.store.setSegmentText('title', 'A edit'); const save = h.store.saveVersion();
      nextDetail(h, { id: 'episode-b' }); await h.store.load('episode-b');
      late.reject(appError('story_version_conflict', 412)); await save; await settle();
      expect(h.store.conflict()).toBeNull(); expect(h.store.error()).toBeNull();
    });

    it('a 409 refetch that returns after the store is destroyed does not resurrect state', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'x');
      const refetch = deferred<EpisodeDetail>(); failWith(h, appError('episode_invalid_state', 409)); h.episodes.scriptGet(() => refetch.promise);
      const save = h.store.saveVersion(); await settle();
      TestBed.resetTestingModule(); refetch.resolve(episodeDetail({ nextActions: ['reopen_story'], currentStory: storySnapshot() })); await save;
      expect(h.store.snapshot()).toBeNull(); expect(h.store.invalidState()).toBe(true);
    });

    it('logout during an in-flight load leaves no snapshot, subscription or timers behind', async () => {
      const h = harness(storySnapshot(), ['edit_story']); const read = deferred<EpisodeDetail>(); const fresh = new FakeEpisodesRepository(); fresh.scriptGet(() => read.promise);
      TestBed.configureTestingModule({ providers: [...providers({ ...h, episodes: fresh }), EpisodeDetailStore, StoryEditorStore] });
      const store = TestBed.inject(StoryEditorStore); const pending = store.load(EPISODE_ID);
      h.sessionGeneration.update((value) => value + 1); TestBed.tick();
      read.resolve(episodeDetail({ currentStory: storySnapshot(), nextActions: ['edit_story'] })); await pending; await settle();
      expect(store.snapshot()).toBeNull(); expect(store.paragraphs()).toHaveLength(0); expect(h.realtime.subscriptions.every((entry) => entry.unsubscribed)).toBe(true);
      store.setSegmentText('title', 'after logout'); expect(store.title()).toBe('');
    });
  });

  describe('realtime invalidation against a dirty draft', () => {
    it('keeps the dirty draft and baseline when the episode becomes non-editable elsewhere, refuses further edits/saves', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'unsaved words');
      nextDetail(h, { nextActions: ['reopen_story'], status: 'story_approved', currentStory: storySnapshot({ etag: '"sv-approved"' }) });
      h.realtime.emit(EPISODE_ID); vi.advanceTimersByTime(250); await settle();
      expect(h.store.readOnly()).toBe(true); expect(h.store.title()).toBe('unsaved words'); expect(h.store.snapshot()?.etag).toBe(storySnapshot().etag);
      h.store.setSegmentText('title', 'blocked'); expect(h.store.title()).toBe('unsaved words');
      await h.store.saveVersion(); expect(h.stories.calls).toHaveLength(0);
    });

    it('a background refetch that fails keeps both the clean draft and the snapshot', async () => {
      const h = await loaded();
      h.episodes.scriptGet(() => rejected(appError('network_error', 0, { kind: 'network' })));
      h.realtime.emit(EPISODE_ID); vi.advanceTimersByTime(250); await settle();
      expect(h.store.title()).toBe('The lantern'); expect(h.store.snapshot()?.etag).toBe(storySnapshot().etag); expect(h.store.hasUnsavedChanges()).toBe(false);
    });

    it('an invalidation during an open conflict never replaces the draft or clears the conflict', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'mine');
      failWith(h, appError('story_version_conflict', 412)); await h.store.saveVersion();
      nextDetail(h, { currentStory: storySnapshot({ etag: '"sv-other"', content: storyContent({ title: 'theirs' }) }) });
      h.realtime.emit(EPISODE_ID); vi.advanceTimersByTime(250); await settle();
      expect(h.store.title()).toBe('mine'); expect(h.store.conflict()).not.toBeNull(); expect(h.stories.calls).toHaveLength(1);
    });

    it('burst of invalidations across a Save coalesces and never issues a PUT', async () => {
      const h = await loaded(); for (let i = 0; i < 20; i++) h.realtime.emit(EPISODE_ID);
      vi.advanceTimersByTime(250); await settle();
      expect(h.episodes.getCalls).toHaveLength(2); expect(h.stories.calls).toHaveLength(0);
    });
  });

  describe('opaque round trip', () => {
    const facts = [{ source_id: 's-1', span: [0, 3], nested: { a: [1, null, 'é'], b: false } }, 'bare-string', 7, null];
    const structure = { schema: 'story.v1', unicode: 'Đêm', arr: [{ k: null }], nul: null, deep: { n: [1, [2, [3]]] } };
    function rich(): StoryDraftWire {
      return storyContent({
        hook: { text: 'h', keywords: ['k'], fact_refs: facts }, structure,
        paragraphs: [{ beat: 'b0', text: 't0', keywords: ['a', 'b'], fact_refs: facts }, { beat: 'b1', text: 't1', keywords: ['c'], fact_refs: [] }],
        ending: { text: 'e', keywords: ['z'], fact_refs: facts },
      });
    }

    it('edits to text and keywords still send every fact_ref and the structure value-identically', async () => {
      const h = await loaded(storySnapshot({ content: rich() })); h.store.setText(h.store.paragraphs()[1]!.segKey, 'changed'); h.store.updateKeyword(h.store.hookKey(), 0, 'kk');
      await h.store.saveVersion();
      const sent = h.stories.calls[0]!.content;
      expect(sent.hook.fact_refs).toEqual(facts); expect(sent.paragraphs[0]?.fact_refs).toEqual(facts); expect(sent.ending.fact_refs).toEqual(facts);
      expect(sent.structure).toEqual(structure); expect(sent.paragraphs[0]?.beat).toBe('b0'); expect(sent.paragraphs[1]?.text).toBe('changed'); expect(sent.hook.keywords).toEqual(['kk']);
      expect(Object.keys(sent)).toEqual(['title', 'hook', 'paragraphs', 'ending', 'description', 'tags', 'content_warnings', 'structure']);
    });

    it('an unmodified round trip serialises byte-identically (JSON) to the loaded story', async () => {
      const content = rich(); const h = await loaded(storySnapshot({ content }));
      h.store.setSegmentText('title', 'x'); h.store.setSegmentText('title', content.title); await h.store.saveVersion();
      expect(h.stories.calls).toHaveLength(0);
      h.store.setMeta({ description: 'd2' }); await h.store.saveVersion();
      expect(JSON.stringify(h.stories.calls[0]!.content)).toBe(JSON.stringify({ ...content, description: 'd2' }));
    });

    it('setMeta ignores structure patches and does not alias caller arrays', async () => {
      const h = await loaded(storySnapshot({ content: rich() })); const tags = ['one'];
      h.store.setMeta({ structure: { evil: true }, tags }); tags.push('mutated-after');
      await h.store.saveVersion(); const sent = h.stories.calls[0]!.content;
      expect(sent.structure).toEqual(structure); expect(sent.tags).toEqual(['one']);
    });

    it('the PUT body is a snapshot: later edits cannot mutate the already-sent object', async () => {
      const h = await loaded(); h.store.setSegmentText('title', 'sent'); await h.store.saveVersion();
      const sent = h.stories.calls[0]!.content; h.store.setText(h.store.paragraphs()[0]!.segKey, 'later'); h.store.addKeyword(h.store.hookKey(), 'later');
      expect(sent.paragraphs[0]?.text).toBe('Paragraph 1 waits.'); expect(sent.hook.keywords).toEqual(['lantern']);
    });
  });

  describe('keyword boundaries', () => {
    it('refuses out-of-range / non-integer / negative indices without changing the draft', async () => {
      const h = await loaded(); const key = h.store.paragraphs()[0]!.segKey;
      for (const index of [-1, 2, 1.5, Number.NaN, Number.POSITIVE_INFINITY]) { h.store.updateKeyword(key, index, 'x'); h.store.removeKeyword(key, index); }
      expect(h.store.paragraphs()[0]?.keywords()).toEqual(['road', 'mist']); expect(h.store.hasUnsavedChanges()).toBe(false);
    });
    it('ignores an unknown segKey for text and keyword intents and never throws', async () => {
      const h = await loaded(); h.store.setText('nope', 'x'); h.store.addKeyword('nope', 'x'); h.store.updateKeyword('nope', 0, 'x'); h.store.removeKeyword('nope', 0);
      expect(h.store.hasUnsavedChanges()).toBe(false);
    });
    it('the limit hint clears on the next successful edit', async () => {
      const h = await loaded(); const key = h.store.paragraphs()[0]!.segKey; h.store.addKeyword(key, 'third'); expect(h.store.keywordLimitHit()).toBe(key);
      h.store.setText(key, 'changed'); expect(h.store.keywordLimitHit()).toBeNull();
    });
  });

  describe('token counting boundaries', () => {
    it('counts NBSP/ideographic space/newline separated words and ignores empty and whitespace-only text', async () => {
      const h = await loaded(); const key = h.store.paragraphs()[0]!.segKey;
      h.store.setText(key, 'một\u00a0hai\u3000ba\nbốn\t\tnăm'); expect(h.store.segmentTokens().get('p:0')).toBe(5);
      h.store.setText(key, '  \n\t '); expect(h.store.segmentTokens().get('p:0')).toBe(0);
      h.store.setText(key, ''); expect(h.store.segmentTokens().get('p:0')).toBe(0);
      h.store.setText(key, 'Việt'.normalize('NFD') + ' ' + 'Việt'.normalize('NFC')); expect(h.store.segmentTokens().get('p:0')).toBe(2);
    });
  });

  describe('empty-story generation intent', () => {
    it('coalesces clicks and adopts the story from the user-action read', async () => {
      const h = await loaded(null, ['generate_story']);
      expect(h.episodes.generateCalls).toHaveLength(0);
      const pending = deferred<void>(); h.episodes.scriptGenerate(() => pending.promise);
      nextDetail(h, { currentStory: storySnapshot() });
      const first = h.store.generateStory(); await h.store.generateStory();
      expect(h.episodes.generateCalls).toHaveLength(1); expect(h.store.generating()).toBe(true);
      expect(h.episodes.generateCalls[0]?.intent.intent).toBe('story.generate');
      pending.resolve(); await first;
      expect(h.episodes.generateCalls[0]?.intent.isSettled).toBe(true);
      expect(h.episodes.getCalls.at(-1)?.userAction).toBe(true);
      expect(h.store.title()).toBe('The lantern'); expect(h.store.generating()).toBe(false);
      expect(h.store.canGenerate()).toBe(false);
    });
    it('waits for a realtime read when the accepted generation has no story yet', async () => {
      const h = await loaded(null, ['generate_story']);
      nextDetail(h, { currentStory: null, hasCurrentStory: false, status: 'generating', nextActions: ['cancel'] });
      await h.store.generateStory();
      expect(h.store.snapshot()).toBeNull(); expect(h.store.canGenerate()).toBe(false);
      nextDetail(h, { currentStory: storySnapshot() }); h.realtime.emit(EPISODE_ID);
      vi.advanceTimersByTime(250); await settle();
      expect(h.store.snapshot()).not.toBeNull(); expect(h.store.title()).toBe('The lantern');
      expect(h.episodes.generateCalls).toHaveLength(1);
    });
    it('keeps an ambiguous intent for an explicit retry without auto-retrying', async () => {
      const h = await loaded(null, ['generate_story']);
      h.episodes.scriptGenerate(() => rejected(appError('network_error', 0, { kind: 'network' })));
      await h.store.generateStory(); vi.advanceTimersByTime(60_000); await settle();
      expect(h.episodes.generateCalls).toHaveLength(1); expect(h.store.generationError()?.code).toBe('network_error');
      expect(h.episodes.generateCalls[0]?.intent.isSettled).toBe(false);
      nextDetail(h, { currentStory: storySnapshot() }); await h.store.generateStory();
      expect(h.episodes.generateCalls[1]?.key).toBe(h.episodes.generateCalls[0]?.key);
      expect(h.store.generationError()).toBeNull();
    });
    it('settles a known failure and starts a fresh key on the next explicit attempt', async () => {
      const h = await loaded(null, ['generate_story']);
      h.episodes.scriptGenerate(() => rejected(appError('fact_sheet_required', 409)));
      await h.store.generateStory();
      expect(h.episodes.generateCalls[0]?.intent.isSettled).toBe(true);
      expect(h.store.generationError()?.code).toBe('fact_sheet_required');
      nextDetail(h, { currentStory: storySnapshot() }); await h.store.generateStory();
      expect(h.episodes.generateCalls[1]?.key).not.toBe(h.episodes.generateCalls[0]?.key);
    });
    it('never generates without the server action or after the session changes', async () => {
      const h = await loaded(null, ['cancel']); await h.store.generateStory();
      expect(h.episodes.generateCalls).toHaveLength(0);
      await h.store.load(EPISODE_ID); h.sessionGeneration.set(1); await h.store.generateStory();
      expect(h.episodes.generateCalls).toHaveLength(0);
    });
    it('ignores a pending generation completion after a different episode is loaded', async () => {
      const h = await loaded(null, ['generate_story']); const pending = deferred<void>();
      h.episodes.scriptGenerate(() => pending.promise); const first = h.store.generateStory();
      h.episodes.scriptGet(() => Promise.resolve(episodeDetail({ id: 'another-episode', currentStory: storySnapshot() })));
      await h.store.load('another-episode'); const gets = h.episodes.getCalls.length;
      pending.resolve(); await first;
      expect(h.episodes.getCalls).toHaveLength(gets); expect(h.store.title()).toBe('The lantern');
    });
  });

});

// ---------------------------------------------------------------------------------------------------------------
// Page level (real EpisodeDetailStore + StoryEditorStore + StoryReviewPage; only the dialogs are scripted).

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: coreVi, 'core/vi': coreVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});

async function flush(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 8; i++) { fixture.detectChanges(); await fixture.whenStable(); await Promise.resolve(); }
}

async function mountPage(snapshot: StorySnapshot | null, actions: readonly string[], options: { readonly query?: Record<string, string>; readonly status?: string } = {}) {
  const h = harness(snapshot, actions, options.status);
  const params = new BehaviorSubject(convertToParamMap({ id: EPISODE_ID }));
  const query = new BehaviorSubject(convertToParamMap(options.query ?? {}));
  const script = { conflict: [] as string[], confirm: [] as boolean[], conflictOpened: 0, confirmOpened: 0 };
  const conflictDialog = { open: () => { script.conflictOpened++; const choice = script.conflict.shift() ?? 'cancel'; return { afterClosed: () => of(choice) }; } };
  const confirmDialog = { confirm: () => { script.confirmOpened++; const choice = script.confirm.shift() ?? false; return { afterClosed: () => of(choice) }; } };
  await TestBed.configureTestingModule({
    imports: [StoryReviewPage, translations],
    providers: [
      ...providers(h), provideRouter([]),
      { provide: ActivatedRoute, useValue: { paramMap: params, queryParamMap: query, snapshot: { paramMap: params.value, queryParamMap: query.value } } },
      { provide: ConflictDialog, useValue: conflictDialog }, { provide: ConfirmDialog, useValue: confirmDialog },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(StoryReviewPage);
  await flush(fixture);
  const root = fixture.nativeElement as HTMLElement;
  const store = fixture.debugElement.injector.get(StoryEditorStore);
  const type = async (selector: string, value: string): Promise<void> => {
    const target = root.querySelector<HTMLTextAreaElement>(selector); if (!target) throw new Error(`missing ${selector}`);
    target.value = value; target.dispatchEvent(new Event('input', { bubbles: true })); await flush(fixture);
  };
  const clickSave = async (): Promise<void> => {
    const button = root.querySelector<HTMLButtonElement>('.story-save-bar button'); if (!button) throw new Error('missing save button');
    button.click(); await flush(fixture);
  };
  return { ...h, fixture, root, store, params, query, script, type, clickSave };
}

describe('StoryReviewPage QA (real stores)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('QA-1: currentStory null renders the EmptyState WITH the generate action from nextActions (failure row 1)', async () => {
    const page = await mountPage(null, ['generate_story'], { status: 'draft' });
    expect(page.root.querySelector('nts-empty-state')).not.toBeNull();
    expect(page.root.querySelectorAll('textarea')).toHaveLength(0);
    const action = Array.from(page.root.querySelectorAll('nts-empty-state button, .story-review-page button')).find((button) => button.textContent?.includes('Viết kịch bản'));
    expect(action).toBeDefined();
    expect(page.episodes.generateCalls).toHaveLength(0);
    page.episodes.scriptGet(() => Promise.resolve(episodeDetail({ currentStory: storySnapshot(), nextActions: ['edit_story'] })));
    (action as HTMLButtonElement).click(); await flush(page.fixture);
    expect(page.episodes.generateCalls).toHaveLength(1);
    expect(page.root.querySelector('nts-story-editor')).not.toBeNull();
    expect(page.root.querySelector('nts-empty-state')).toBeNull();
  });

  it('keeps the same editable title control during a background detail read', async () => {
    const page = await mountPage(storySnapshot(), ['edit_story']);
    const title = page.root.querySelector<HTMLTextAreaElement>('#seg-title');
    const pending = deferred<EpisodeDetail>();
    page.episodes.scriptGet(() => pending.promise);
    const detail = page.fixture.debugElement.injector.get(EpisodeDetailStore);
    const read = detail.refetch(); await flush(page.fixture);
    expect(detail.loading()).toBe(true);
    expect(page.root.querySelector('#seg-title')).toBe(title);
    await page.type('#seg-title', 'Preserved while reading');
    pending.resolve(episodeDetail({ currentStory: storySnapshot(), nextActions: ['edit_story'] }));
    await read; await flush(page.fixture);
    expect(page.store.title()).toBe('Preserved while reading');
    expect(page.root.querySelector('#seg-title')).toBe(title);
  });

  it('shows a mapped generation error in the empty state without retrying automatically', async () => {
    const page = await mountPage(null, ['generate_story'], { status: 'draft' });
    page.episodes.scriptGenerate(() => rejected(appError('fact_sheet_required', 409)));
    page.root.querySelector<HTMLButtonElement>('nts-empty-state button')!.click(); await flush(page.fixture);
    expect(page.root.querySelector('nts-form-error-alert')?.textContent).toContain(errorsVi.fact_sheet_required);
    expect(page.root.querySelector('nts-empty-state button')).not.toBeNull();
    expect(page.episodes.generateCalls).toHaveLength(1);
  });

  it('omits the empty-state generate action when nextActions does not authorize it', async () => {
    const page = await mountPage(null, ['cancel'], { status: 'draft' });
    expect(page.root.querySelector('nts-empty-state')).not.toBeNull();
    expect(page.root.querySelector('nts-empty-state button')).toBeNull();
    await page.store.generateStory(); expect(page.episodes.generateCalls).toHaveLength(0);
  });


  it('currentStory null never renders an editor, textarea or save button', async () => {
    const page = await mountPage(null, ['generate_story'], { status: 'draft' });
    expect(page.root.querySelector('nts-empty-state')).not.toBeNull();
    expect(page.root.querySelector('nts-story-editor')).toBeNull(); expect(page.root.querySelectorAll('textarea')).toHaveLength(0);
    expect(page.root.querySelector('.story-save-bar')).toBeNull();
  });

  it('Reload latest + confirm CANCEL keeps the dirty draft, closes the conflict and issues no reload re-read', async () => {
    const page = await mountPage(storySnapshot(), ['edit_story']);
    await page.type('#seg-title', 'my words');
    page.stories.scriptSave(() => rejected(appError('story_version_conflict', 412))); page.script.conflict.push('reload'); page.script.confirm.push(false);
    // The 412 diff read answers with the server copy: the dialog opens on a real diff and the episode stays editable.
    page.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], status: 'story_review', currentStory: storySnapshot({ etag: '"sv-server"', content: storyContent({ title: 'Server copy' }) }) })));
    const gets = page.episodes.getCalls.length;
    await page.clickSave(); await flush(page.fixture);
    // Declining the confirm is not a decision about the conflict, so the dialog comes back — one confirm, one diff read.
    expect(page.script.conflictOpened).toBe(2); expect(page.script.confirmOpened).toBe(1);
    expect(page.store.title()).toBe('my words'); expect(page.store.hasUnsavedChanges()).toBe(true); expect(page.store.conflict()).toBeNull();
    // Only the 412 diff read happens (user-authorised); declining the reload confirm adds no further re-read.
    expect(page.episodes.getCalls.length).toBe(gets + 1);
    expect(page.episodes.getCalls.at(-1)?.userAction).toBe(true);
    expect((page.root.querySelector('#seg-title') as HTMLTextAreaElement).value).toBe('my words');
  });

  it('Reload latest + confirm ACCEPT adopts the server content with a user-action read and clears dirty', async () => {
    const page = await mountPage(storySnapshot(), ['edit_story']);
    await page.type('#seg-title', 'my words');
    page.stories.scriptSave(() => rejected(appError('story_version_conflict', 412))); page.script.conflict.push('reload'); page.script.confirm.push(true);
    // First the 412 diff read, then the user-authorised reload: both answer with the server copy.
    page.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], status: 'story_review', currentStory: storySnapshot({ etag: '"sv-server"', content: storyContent({ title: 'Server copy' }) }) })));
    page.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], status: 'story_review', currentStory: storySnapshot({ etag: '"sv-server"', content: storyContent({ title: 'Server copy' }) }) })));
    await page.clickSave(); await flush(page.fixture);
    expect(page.store.title()).toBe('Server copy'); expect(page.store.hasUnsavedChanges()).toBe(false); expect(page.store.snapshot()?.etag).toBe('"sv-server"');
    expect(page.episodes.getCalls.at(-1)?.userAction).toBe(true); expect((page.root.querySelector('#seg-title') as HTMLTextAreaElement).value).toBe('Server copy');
    expect(page.script.conflictOpened).toBe(1);
  });

  it('a repeated 412 on Save-as-new reopens the dialog with the draft intact; Cancel then ends the loop', async () => {
    const page = await mountPage(storySnapshot(), ['edit_story']);
    await page.type('#seg-title', 'my words');
    page.stories.scriptSave(() => rejected(appError('story_version_conflict', 412))); page.stories.scriptSave(() => rejected(appError('story_version_conflict', 412)));
    page.script.conflict.push('saveAsNew', 'cancel');
    // First the 412 diff read, then the Save-as-new fresh-etag read: both answer with the fresh ETag.
    page.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], status: 'story_review', currentStory: storySnapshot({ etag: '"sv-fresh"' }) })));
    page.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], status: 'story_review', currentStory: storySnapshot({ etag: '"sv-fresh"' }) })));
    await page.clickSave(); await flush(page.fixture);
    expect(page.script.conflictOpened).toBe(2); expect(page.stories.calls).toHaveLength(2); expect(page.stories.calls[1]?.etag).toBe('"sv-fresh"');
    expect(page.store.title()).toBe('my words'); expect(page.store.conflict()).toBeNull(); expect(page.store.hasUnsavedChanges()).toBe(true);
  });

  it('QA-2: Save-as-new that finds the episode non-editable must not re-open the conflict dialog over a read-only page', async () => {
    const page = await mountPage(storySnapshot(), ['edit_story']);
    await page.type('#seg-title', 'my words');
    page.stories.scriptSave(() => rejected(appError('story_version_conflict', 412))); page.script.conflict.push('saveAsNew', 'cancel');
    // The 412 diff read still finds the episode editable, so the dialog opens and the user's own Save as new is what
    // discovers the change: the second read reports the episode as no longer editable.
    page.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['edit_story'], status: 'story_review', currentStory: storySnapshot({ etag: '"sv-theirs"' }) })));
    page.episodes.scriptGet(() => Promise.resolve(episodeDetail({ nextActions: ['reopen_story'], status: 'story_approved', currentStory: storySnapshot({ etag: '"sv-fresh"' }) })));
    await page.clickSave(); await flush(page.fixture);
    expect(page.store.readOnly()).toBe(true);
    expect(page.script.conflictOpened).toBe(1);
    expect(page.store.conflict()).toBeNull(); expect(page.store.invalidState()).toBe(true);
    expect(page.stories.calls).toHaveLength(1); expect(page.store.title()).toBe('my words');
  });

  it.each(['p:99', 'p:03', 'p:-1', 'p:1e3', 'p:', 'p:3 ', ' p:3', 'P:3', 'paragraphs[3]', 'p:0x1', '__proto__', 'constructor', 'title\n', 'hook;', '', 'p:9007199254740993', 'ending/../meta'])(
    'invalid deep link ?segment=%j focuses nothing, throws nothing and leaves the editor usable',
    async (segment) => {
      const page = await mountPage(storySnapshot(), ['edit_story'], { query: { segment } });
      expect(page.root.querySelectorAll('textarea').length).toBeGreaterThan(4);
      expect(page.root.contains(document.activeElement) && document.activeElement?.tagName === 'TEXTAREA').toBe(false);
      await page.type('#seg-title', 'still editable'); expect(page.store.title()).toBe('still editable');
    },
  );

  it('a valid deep link re-focuses when only the query parameter changes, including meta and a canonical p:0', async () => {
    const page = await mountPage(storySnapshot(), ['edit_story'], { query: { segment: 'p:2' } });
    expect(document.activeElement).toBe(page.root.querySelector('#seg-p-2'));
    page.query.next(convertToParamMap({ segment: 'meta' })); await flush(page.fixture);
    expect(document.activeElement).toBe(page.root.querySelector('#seg-meta'));
    page.query.next(convertToParamMap({ segment: 'p:0' })); await flush(page.fixture);
    expect(document.activeElement).toBe(page.root.querySelector('#seg-p-0'));
    expect(page.episodes.getCalls).toHaveLength(1);
  });

  it('read-only (no edit_story) renders readonly textareas, no Save and no enabled scene controls', async () => {
    const page = await mountPage(storySnapshot(), ['reopen_story'], { status: 'story_approved' });
    const areas = Array.from(page.root.querySelectorAll<HTMLTextAreaElement>('textarea')); expect(areas.length).toBeGreaterThan(4);
    expect(areas.every((area) => area.readOnly)).toBe(true);
    expect(page.root.querySelector('.story-save-bar button')).toBeNull();
    expect(Array.from(page.root.querySelectorAll<HTMLButtonElement>('.segment-chips button')).filter((button) => !button.disabled)).toHaveLength(0);
    await page.type('#seg-title', 'ignored'); expect(page.store.title()).toBe('The lantern');
  });
});
