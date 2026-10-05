import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { describe, expect, it } from 'vitest';
import { FakeSessionApi } from '../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { CatalogRepository } from './application/catalog.repository';
import { SeriesDetailStore } from './application/series-detail.store';
import { SeriesEditorStore } from './application/series-editor.store';
import { SeriesListStore } from './application/series-list.store';
import { SeriesRepository } from './application/series.repository';
import { VoiceStepStore } from './application/voice-step.store';
import type { Series } from './domain/series.model';
import { FakeCatalogRepository } from './testing/fake-catalog.repository';
import { CANNED_SERIES, FakeSeriesRepository } from './testing/fake-series.repository';

// Independent QA of 10.4.2-T1 (series list + 4-step editor + style grid). Every test asserts what the consumer of the
// store sees (form values, outcomes, requests the server received), never how the store is wired. Tests that found a
// defect were filed with a `DEFECT_` prefix while red; the fixes keep the same assertions under their plain name.

class FakeAnnouncer {
  readonly messages: string[] = [];
  announce(message: string): Promise<void> {
    this.messages.push(message);
    return Promise.resolve();
  }
}

function appError(code: string, status: number, kind: AppError['kind'], extra: Partial<AppError> = {}): AppError {
  return { code, status, kind, fieldErrors: {}, ...extra };
}

function cannedRow(id: string): Series {
  const row = CANNED_SERIES.find((series) => series.id === id);
  if (row === undefined) throw new Error(`no canned row ${id}`);
  return { ...row };
}

const translations = TranslocoTestingModule.forRoot({
  langs: {
    vi: {
      series: {
        voice: { clearedByLanguage: 'Giọng đọc đã được xoá vì không khớp ngôn ngữ' },
        style: { clearedByTopic: 'Phong cách đã được xoá vì không hợp chủ đề' },
      },
    },
  },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi' },
  preloadLangs: true,
});

interface Setup {
  readonly store: SeriesEditorStore;
  readonly seriesRepo: FakeSeriesRepository;
  readonly catalog: FakeCatalogRepository;
  readonly announcer: FakeAnnouncer;
  readonly session: SessionStore;
}

function setup(): Setup {
  const seriesRepo = new FakeSeriesRepository();
  const catalog = new FakeCatalogRepository();
  const announcer = new FakeAnnouncer();
  TestBed.configureTestingModule({
    imports: [translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      { provide: SessionApi, useValue: new FakeSessionApi() },
      { provide: SeriesRepository, useValue: seriesRepo },
      { provide: CatalogRepository, useValue: catalog },
      { provide: LiveAnnouncer, useValue: announcer },
      SeriesEditorStore,
      VoiceStepStore,
    ],
  });
  return { store: TestBed.inject(SeriesEditorStore), seriesRepo, catalog, announcer, session: TestBed.inject(SessionStore) };
}

describe('SeriesEditorStore (QA, 10.4.2-T1): edit saves', () => {
  it('Edit_SaveThenRevertToTheOriginalValue_StillSendsThePatch', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    const original = 'Chuyện ma đêm khuya';

    store.form.controls.title.setValue('Tên mới');
    expect(await store.submit()).toEqual({ kind: 'saved' });
    expect(seriesRepo.stored('s-ghost')?.title).toBe('Tên mới');

    // The user changes their mind and types the original title again: the server still holds "Tên mới".
    store.form.controls.title.setValue(original);
    expect(await store.submit()).toEqual({ kind: 'saved' });
    expect(seriesRepo.stored('s-ghost')?.title, 'the second save must reach the server').toBe(original);
  });

  it('Edit_SecondSave_SendsOnlyTheFieldsChangedSinceTheFirstSave', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Tên mới');
    expect(await store.submit()).toEqual({ kind: 'saved' });

    store.form.controls.aspect.setValue('portrait');
    expect(await store.submit()).toEqual({ kind: 'saved' });
    expect(seriesRepo.patchCalls).toHaveLength(2);
    expect(seriesRepo.patchCalls[1]?.patch, 'title was already saved and must not be re-sent').toEqual({ aspect: 'portrait' });
    expect(seriesRepo.patchCalls[1]?.etag).toBe('"se-42"');
  });

  it('Edit_TopicLocked_AfterAnEarlierSave_RevertsToTheTopicTheServerHolds', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.topic.setValue('spiritual');
    expect(await store.submit()).toEqual({ kind: 'saved' });
    expect(seriesRepo.stored('s-ghost')?.topic).toBe('spiritual');

    // Autopilot was switched on elsewhere; the next topic edit is refused with 409 series_topic_locked.
    store.form.controls.topic.setValue('ghost');
    seriesRepo.failNext(appError('series_topic_locked', 409, 'precondition'));
    expect(await store.submit()).toEqual({ kind: 'failed' });
    expect(store.form.controls.topic.value, 'the form must show the stored (server) topic').toBe('spiritual');
  });

  it('Edit_LateInitResponseForAPreviousId_IsDiscarded', async () => {
    const { store, seriesRepo } = setup();
    seriesRepo.defer();
    const first = store.init('edit', 's-ghost');
    seriesRepo.defer(false);
    await store.init('edit', 's-spiritual');
    seriesRepo.resolveAll();
    await first;
    expect(store.seriesId()).toBe('s-spiritual');
    expect(store.form.controls.title.value).toBe('Đền chùa kể chuyện');
  });

  it('Edit_ConcurrencyConflict_OpensTheSameDialogStateWithItsTraceId', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.failNext(appError('concurrency_conflict', 409, 'conflict', { traceId: 'NTS-TRACE-1' }));

    expect(await store.submit()).toEqual({ kind: 'conflict' });
    expect(store.conflict()).toBe(true);
    expect(store.conflictCode()).toBe('concurrency_conflict');
    expect(store.conflictTraceId()).toBe('NTS-TRACE-1');
    expect(store.form.controls.title.value).toBe('Đổi tên');
  });

  it('Edit_428_IsAFailureShownOnce_NeverRetried_AndKeepsTheValues', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.failNext(appError('if_match_required', 428, 'client_bug'));

    expect(await store.submit()).toEqual({ kind: 'failed' });
    expect(store.error()?.code).toBe('if_match_required');
    expect(store.conflict()).toBe(false);
    expect(store.form.controls.title.value).toBe('Đổi tên');
    expect(seriesRepo.patchCalls, 'a 428 is a client bug and is never auto-retried').toHaveLength(1);
  });

  it('Edit_ConflictThenSaveMine_SendsOnlyTheFreshServerEtagAsIfMatch', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.advanceVersion('s-ghost');
    seriesRepo.advanceVersion('s-ghost');
    expect(await store.submit()).toEqual({ kind: 'conflict' });

    expect(await store.resolveConflict('saveAsNew')).toEqual({ kind: 'saved' });
    // The second tag is the one `GET` returned (se-43), never a client-computed "next" of the stale one (se-42).
    expect(seriesRepo.patchCalls.map((call) => call.etag)).toEqual(['"se-41"', '"se-43"']);
  });

  it('Edit_SaveMine_WhenTheSeriesGotArchivedMeanwhile_ShowsArchivedAndKeepsValues', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.advanceVersion('s-ghost');
    expect(await store.submit()).toEqual({ kind: 'conflict' });
    // Another tab archives the series before the user picks "Save mine as new version".
    await seriesRepo.archive('s-ghost');

    expect(await store.resolveConflict('saveAsNew')).toEqual({ kind: 'failed' });
    expect(store.archived()).toBe(true);
    expect(store.canSave()).toBe(false);
    expect(store.error()?.code).toBe('series_archived');
    expect(store.form.controls.title.value).toBe('Đổi tên');
  });

  it('Edit_ConflictReload_ThenAnotherEdit_PatchesAgainstTheServerEtag', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.advanceVersion('s-ghost');
    expect(await store.submit()).toEqual({ kind: 'conflict' });
    await store.resolveConflict('reload');

    store.form.controls.aspect.setValue('portrait');
    expect(await store.submit()).toEqual({ kind: 'saved' });
    expect(seriesRepo.patchCalls[1]?.etag).toBe('"se-42"');
    expect(seriesRepo.patchCalls[1]?.patch).toEqual({ aspect: 'portrait' });
  });
});

describe('SeriesEditorStore (QA, 10.4.2-T1): style and topic', () => {
  it('Edit_TopicChangedToTrueCrimeBeforeStep4_ClearsTheFictionStyle', async () => {
    const { store, announcer } = setup();
    await store.init('edit', 's-ghost');
    expect(store.form.controls.styleTemplateId.value).toBe('t-dark-folklore-ink');

    // The creator changes the topic on step 1 and has not opened step 4 yet (the templates are not loaded).
    await store.setTopic('true_crime');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });

    const selectedStyle = store.form.controls.styleTemplateId.value;
    const visibleIds = store.templates().map((template) => template.id);
    expect(visibleIds).toEqual(['t-noir-sketch', 't-case-file-illustration']);
    expect(selectedStyle, 'a fiction style must not stay selected under true_crime').toBeNull();
    expect(announcer.messages).toContain('Phong cách đã được xoá vì không hợp chủ đề');
  });

  it('Edit_TrueCrimeSeriesChangedToGhostBeforeStep4_ClearsTheTrueCrimeStyle', async () => {
    const { store } = setup();
    await store.init('edit', 's-true-crime');
    await store.setTopic('ghost');
    expect(store.form.controls.styleTemplateId.value).toBeNull();
  });

  it('Edit_InactiveStoredStyle_TopicChangeBetweenFictionTopics_LeavesNoHiddenSelection', async () => {
    const { store, seriesRepo } = setup();
    seriesRepo.willServe([{ ...cannedRow('s-ghost'), styleTemplateId: 't-removed' }]);
    await store.init('edit', 's-ghost');
    await store.goTo(4);
    expect(store.templates().some((template) => template.id === 't-removed')).toBe(true);

    await store.setTopic('spiritual');
    const selected = store.form.controls.styleTemplateId.value;
    const visible = store.templates().some((template) => template.id === selected);
    expect(selected === null || visible, `selected style ${String(selected)} is not on the grid and not cleared`).toBe(true);
  });

  it('Templates_OutOfOrderResponses_KeepOnlyTheCurrentTopicsList', async () => {
    const { store, catalog } = setup();
    await store.init('create', null);
    catalog.deferTemplates();
    const spiritual = store.setTopic('spiritual');
    const trueCrime = store.setTopic('true_crime');
    expect(catalog.pendingTemplateCount).toBe(2);

    catalog.resolveTemplate('true_crime');
    await trueCrime;
    catalog.resolveTemplate('spiritual');
    await spiritual;

    expect(store.templates().map((template) => template.id)).toEqual(['t-noir-sketch', 't-case-file-illustration']);
    expect(store.loadingTemplates()).toBe(false);
  });

  it('Templates_LateResponseAfterSessionChange_IsDiscarded', async () => {
    const { store, catalog, session } = setup();
    await store.init('create', null);
    catalog.deferTemplates();
    const loading = store.setTopic('spiritual');
    session.reset();
    catalog.resolveNextTemplate();
    await loading;
    expect(store.templates()).toEqual([]);
  });

  it('Edit_TopicLockedByAutopilot_NeverRequestsTemplatesNorChangesTheStyle', async () => {
    const { store, catalog } = setup();
    await store.init('edit', 's-spiritual');
    await store.setTopic('ghost');
    expect(store.form.controls.topic.value).toBe('spiritual');
    expect(store.form.controls.styleTemplateId.value).toBe('t-watercolor-night');
    expect(catalog.styleTemplateCalls).toEqual([]);
  });
});

describe('SeriesEditorStore (QA, 10.4.2-T1): create', () => {
  async function reachSubmit(store: SeriesEditorStore): Promise<void> {
    await store.init('create', null);
    store.form.controls.title.setValue('  Chuyện ma  ');
    await store.goTo(3);
    store.setVoice('vi-quiet');
    await store.goTo(4);
    store.setStyleTemplate('t-foggy-cinematic');
  }

  it('Title_WhitespaceOnly_KeepsStep1InvalidAndNextDisabled', async () => {
    const { store, seriesRepo } = setup();
    await store.init('create', null);
    store.form.controls.title.setValue('     ');
    expect(store.stepValid(1)).toBe(false);
    expect(store.canGoNext()).toBe(false);
    expect(await store.submit()).toEqual({ kind: 'invalid', step: 1 });
    expect(seriesRepo.createCalls).toEqual([]);
  });

  it('Title_121Characters_IsInvalid_And120IsSentTrimmed', async () => {
    const { store, seriesRepo } = setup();
    await reachSubmit(store);
    store.form.controls.title.setValue('a'.repeat(121));
    expect(store.form.controls.title.hasError('maxlength')).toBe(true);
    expect(await store.submit()).toEqual({ kind: 'invalid', step: 1 });

    store.form.controls.title.setValue('a'.repeat(120));
    expect(await store.submit()).toEqual({ kind: 'created', id: 's-created-5' });
    expect(seriesRepo.createCalls[0]?.title).toBe('a'.repeat(120));
  });

  it('Create_SendsTrimmedTitleNullPresetAndNoLanguageDrift_AndKeepsTheServerEtag', async () => {
    const { store, seriesRepo } = setup();
    await reachSubmit(store);
    expect(await store.submit()).toEqual({ kind: 'created', id: 's-created-5' });
    expect(seriesRepo.createCalls).toEqual([
      {
        title: 'Chuyện ma',
        topic: 'ghost',
        language: 'vi',
        aspect: 'landscape',
        voiceId: 'vi-quiet',
        styleTemplateId: 't-foggy-cinematic',
        storyPreset: null,
      },
    ]);
    expect(store.isDirty()).toBe(false);
  });

  it('Create_DoubleSubmit_IssuesOneRequestAndTheSecondCallSharesTheOutcome', async () => {
    const { store, seriesRepo } = setup();
    await reachSubmit(store);
    seriesRepo.defer();
    const first = store.submit();
    const second = store.submit();
    expect(seriesRepo.createCalls).toHaveLength(1);
    seriesRepo.resolveAll();
    expect(await second).toEqual(await first);
    expect(seriesRepo.createCalls).toHaveLength(1);
  });

  it('Create_VoiceLanguageMismatch_ClearsVoice_OpensStep3_FocusesIt_AndReloadsVoices', async () => {
    const { store, seriesRepo, catalog } = setup();
    await reachSubmit(store);
    const voiceCallsBefore = catalog.voicedCalls.length;
    seriesRepo.failNext(appError('voice_language_mismatch', 400, 'validation'));

    expect(await store.submit()).toEqual({ kind: 'failed' });
    expect(store.form.controls.voiceId.value).toBeNull();
    expect(store.step()).toBe(3);
    expect(store.focusRequest()?.control).toBe('voiceId');
    expect(store.stepValid(3)).toBe(false);
    expect(catalog.voicedCalls.length).toBeGreaterThan(voiceCallsBefore);
  });

  it('Create_ValidationFailedOnStyle_OpensStep4_AndTheEnteredValuesRemain', async () => {
    const { store, seriesRepo } = setup();
    await reachSubmit(store);
    seriesRepo.failNext(appError('validation_failed', 400, 'validation', { fieldErrors: { styleTemplateId: ['errors.style'] } }));

    expect(await store.submit()).toEqual({ kind: 'invalid', step: 4 });
    expect(store.step()).toBe(4);
    expect(store.form.controls.title.value).toBe('  Chuyện ma  ');
    expect(store.form.controls.voiceId.value).toBe('vi-quiet');
  });

  it('Create_NetworkError_KeepsTheFormAndAllowsARetryWithoutDuplicates', async () => {
    const { store, seriesRepo } = setup();
    await reachSubmit(store);
    seriesRepo.failNext(appError('network_error', 0, 'network'));
    expect(await store.submit()).toEqual({ kind: 'failed' });
    expect(store.error()?.code).toBe('network_error');
    expect(store.saving()).toBe(false);

    expect(await store.submit()).toEqual({ kind: 'created', id: 's-created-5' });
    expect(seriesRepo.createCalls).toHaveLength(2);
    expect(store.error()).toBeNull();
  });
});

describe('SeriesDetailStore (QA, 10.4.2-T1)', () => {
  function setupDetail(): { readonly store: SeriesDetailStore; readonly seriesRepo: FakeSeriesRepository } {
    const seriesRepo = new FakeSeriesRepository();
    TestBed.configureTestingModule({
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        { provide: SessionApi, useValue: new FakeSessionApi() },
        { provide: SeriesRepository, useValue: seriesRepo },
        SeriesDetailStore,
      ],
    });
    return { store: TestBed.inject(SeriesDetailStore), seriesRepo };
  }

  it('Load_FailureForADifferentId_DoesNotKeepShowingThePreviousSeries', async () => {
    const { store, seriesRepo } = setupDetail();
    await store.load('s-ghost');
    expect(store.series()?.id).toBe('s-ghost');

    seriesRepo.failNext(appError('internal_error', 500, 'server'));
    await store.load('s-spiritual');
    expect(store.error()?.code).toBe('internal_error');
    // The page renders `series` before `error`, so a leftover series would hide the error behind another id's data.
    expect(store.series()).toBeNull();
  });

  it('Load_LateResponseForAPreviousId_IsDiscarded', async () => {
    const { store, seriesRepo } = setupDetail();
    seriesRepo.defer();
    const first = store.load('s-ghost');
    seriesRepo.defer(false);
    await store.load('s-spiritual');
    seriesRepo.resolveAll();
    await first;
    expect(store.series()?.id).toBe('s-spiritual');
    expect(store.loading()).toBe(false);
  });

  it('Load_Archived404_ClearsThePreviousSeries', async () => {
    const { store } = setupDetail();
    await store.load('s-ghost');
    await store.load('missing');
    expect(store.notFound()).toBe(true);
    expect(store.series()).toBeNull();
  });
});

describe('SeriesListStore (QA, 10.4.2-T1)', () => {
  function setupList(): { readonly store: SeriesListStore; readonly seriesRepo: FakeSeriesRepository } {
    const seriesRepo = new FakeSeriesRepository();
    TestBed.configureTestingModule({
      imports: [translations],
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        { provide: SessionApi, useValue: new FakeSessionApi() },
        { provide: SeriesRepository, useValue: seriesRepo },
        { provide: LiveAnnouncer, useValue: new FakeAnnouncer() },
        SeriesListStore,
      ],
    });
    return { store: TestBed.inject(SeriesListStore), seriesRepo };
  }

  // Task §Failure handling, row "Cursor tampered with / garbage": a 400 on a cursor page drops the cursor and the
  // first page is loaded again — through `retry()` (the same row `series-list.store.spec.ts` covers).
  it('List_ValidationFailedOnACursorPage_RetryDropsTheCursorAndReloadsPage1', async () => {
    const { store, seriesRepo } = setupList();
    await store.load();
    seriesRepo.failNext(appError('validation_failed', 400, 'validation'));
    await store.loadMore();
    expect(seriesRepo.listCalls[1]?.cursor).toBe('p2');
    expect(store.error()?.code).toBe('validation_failed');

    await store.retry();
    expect(seriesRepo.listCalls[2]?.cursor, 'the cursor was dropped, not retried').toBeNull();
    expect(store.items().map((item) => item.id)).toEqual(['s-ghost', 's-spiritual']);
  });
});
