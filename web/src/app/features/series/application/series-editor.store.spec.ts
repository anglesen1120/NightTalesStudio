import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { vi } from 'vitest';
import type { AppError } from '@core/errors';
import { SessionApi, SessionStore } from '@core/session';
import { FakeSessionApi } from '../../../core/session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import type { Language } from '../domain/voice.model';
import type { Series, Topic } from '../domain/series.model';
import { CANNED_SERIES, FakeSeriesRepository } from '../testing/fake-series.repository';
import { FakeCatalogRepository } from '../testing/fake-catalog.repository';
import { CatalogRepository } from './catalog.repository';
import { DIRTY_DEBOUNCE_MS, SeriesEditorStore } from './series-editor.store';
import { SeriesRepository } from './series.repository';
import { VoiceStepStore } from './voice-step.store';

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

/** Lets the fire-and-forget translation lookup and announcement settle before a message assertion. */
async function flush(): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

function cannedRow(id: string): Series {
  const row = CANNED_SERIES.find((series) => series.id === id);
  if (row === undefined) throw new Error(`no canned row ${id}`);
  return { ...row };
}

/** The templates the fake serves for a fiction topic: three `ghost` and two `spiritual` (03.1.1-T1 seed). */
const FICTION_TEMPLATE_IDS = [
  't-dark-folklore-ink',
  't-foggy-cinematic',
  't-vintage-photo-grain',
  't-watercolor-night',
  't-temple-candlelight',
];
const TRUE_CRIME_TEMPLATE_IDS = ['t-noir-sketch', 't-case-file-illustration'];

const translations = TranslocoTestingModule.forRoot({
  langs: {
    vi: {
      series: {
        voice: { clearedByLanguage: 'Giọng đọc đã được xoá vì không khớp ngôn ngữ' },
        style: { clearedByTopic: 'Phong cách đã được xoá vì không hợp chủ đề' },
      },
      errors: { validation_failed: 'Dữ liệu không hợp lệ' },
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
  readonly voiceStep: VoiceStepStore;
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
  return {
    store: TestBed.inject(SeriesEditorStore),
    seriesRepo,
    catalog,
    announcer,
    voiceStep: TestBed.inject(VoiceStepStore),
    session: TestBed.inject(SessionStore),
  };
}

/** Step 1 filled: a valid title on the deck's default topic and language. */
function fillStep1(store: SeriesEditorStore): void {
  store.form.controls.title.setValue('  Chuyện ma đêm khuya  ');
}

async function reachStep4(store: SeriesEditorStore): Promise<void> {
  fillStep1(store);
  await store.goTo(3);
  store.setVoice('vi-quiet');
  await store.goTo(4);
}

describe('SeriesEditorStore', () => {
  it('Step1_RequiresTitleAndTopic', async () => {
    const { store } = setup();
    await store.init('create', null);
    expect(store.stepValid(1)).toBe(false);
    expect(store.firstInvalidStep()).toBe(1);

    store.form.controls.title.setValue('Chuyện ma');
    expect(store.stepValid(1)).toBe(true);

    // A non-nullable control can still be emptied by a hostile caller; the required validator must catch it.
    store.form.controls.topic.setValue(null as unknown as Topic);
    expect(store.stepValid(1)).toBe(false);
    store.form.controls.topic.setValue('ghost');
    expect(store.stepValid(1)).toBe(true);

    store.form.controls.language.setValue(null as unknown as Language);
    expect(store.stepValid(1)).toBe(false);
    store.form.controls.language.setValue('vi');
    expect(store.stepValid(1)).toBe(true);
  });

  it('Title_Boundaries_1And120Ok_0And121Rejected', async () => {
    const { store } = setup();
    await store.init('create', null);
    const title = store.form.controls.title;

    title.setValue('');
    expect(title.valid).toBe(false);
    title.setValue('   ');
    expect(title.valid).toBe(false);
    title.setValue('x');
    expect(title.valid).toBe(true);
    title.setValue('x'.repeat(120));
    expect(title.valid).toBe(true);
    title.setValue('x'.repeat(121));
    expect(title.valid).toBe(false);
    expect(title.hasError('maxlength')).toBe(true);
  });

  it('StepOrder_NextBack', async () => {
    const { store } = setup();
    await store.init('create', null);

    await store.goNext();
    expect(store.step()).toBe(1);
    expect(store.canGoNext()).toBe(false);

    fillStep1(store);
    expect(store.canGoNext()).toBe(true);
    await store.goNext();
    expect(store.step()).toBe(2);

    await store.goNext();
    expect(store.step()).toBe(3);
    await store.goNext();
    expect(store.step()).toBe(3);
    expect(store.canGoNext()).toBe(false);

    store.setVoice('vi-quiet');
    await store.goNext();
    expect(store.step()).toBe(4);
    await store.goNext();
    expect(store.step()).toBe(4);

    store.goBack();
    expect(store.step()).toBe(3);
    store.goBack();
    expect(store.step()).toBe(2);
  });

  it('Step1_GoTo_CannotPassFirstInvalidStep', async () => {
    const { store } = setup();
    await store.init('create', null);
    fillStep1(store);
    expect(store.firstInvalidStep()).toBe(3);

    await store.goTo(4);
    expect(store.step()).toBe(1);
    await store.goTo(3);
    expect(store.step()).toBe(3);
    await store.goTo(4);
    expect(store.step()).toBe(3);

    store.setVoice('vi-quiet');
    expect(store.firstInvalidStep()).toBe(4);
    await store.goTo(4);
    // The first invalid step is reachable, one past it is not; the step itself stays unsatisfied until a style is
    // picked — `styleTemplateId` starts null and is required (series-form.ts) and nothing is auto-selected (see
    // SetTopic_ClearsIncompatibleStyle_KeepsCompatible, which pins that a reload leaves the selection alone).
    expect(store.step()).toBe(4);
    expect(store.firstInvalidStep()).toBe(4);
    expect(store.canGoNext()).toBe(false);
  });

  it('SetLanguage_ClearsVoice', async () => {
    const { store, catalog, voiceStep, announcer } = setup();
    await store.init('create', null);
    fillStep1(store);
    await store.goTo(3);
    store.setVoice('vi-quiet');
    expect(store.form.controls.voiceId.value).toBe('vi-quiet');
    expect(voiceStep.selectedId()).toBe('vi-quiet');

    await store.setLanguage('en');
    expect(store.form.controls.voiceId.value).toBeNull();
    expect(voiceStep.selectedId()).toBeNull();
    // `vi` is the load when step 3 is entered (plan §Performance), `en` the language change's own load.
    expect(catalog.voicedCalls).toEqual(['vi', 'en']);
    await flush();
    expect(announcer.messages).toEqual(['Giọng đọc đã được xoá vì không khớp ngôn ngữ']);
  });

  it('SetTopic_ClearsIncompatibleStyle_KeepsCompatible', async () => {
    const { store, announcer } = setup();
    const cleared = 'Phong cách đã được xoá vì không hợp chủ đề';
    await store.init('create', null);
    await reachStep4(store);
    expect(store.templates().map((template) => template.id)).toEqual(FICTION_TEMPLATE_IDS);

    store.setStyleTemplate('t-dark-folklore-ink');
    await store.setTopic('true_crime');
    expect(store.form.controls.styleTemplateId.value).toBeNull();
    expect(store.templates().map((template) => template.id)).toEqual(TRUE_CRIME_TEMPLATE_IDS);
    await flush();
    expect(announcer.messages).toEqual([cleared]);

    store.setStyleTemplate('t-noir-sketch');
    await store.setTopic('ghost');
    expect(store.form.controls.styleTemplateId.value).toBeNull();
    expect(store.templates().map((template) => template.id)).toEqual(FICTION_TEMPLATE_IDS);
    await flush();
    expect(announcer.messages).toEqual([cleared, cleared]);

    // A `spiritual` template stays valid under `ghost` and under `spiritual`: nothing is cleared, nothing announced.
    store.setStyleTemplate('t-watercolor-night');
    await store.setTopic('spiritual');
    expect(store.form.controls.styleTemplateId.value).toBe('t-watercolor-night');
    await flush();
    expect(announcer.messages).toEqual([cleared, cleared]);
  });

  it('Templates_OneRequestPerTopicChange_AndPreviousListKeptWhileLoading', async () => {
    const { store, catalog } = setup();
    await store.init('create', null);
    fillStep1(store);

    await store.setTopic('true_crime');
    expect(catalog.styleTemplateCalls).toEqual(['true_crime']);
    await store.setTopic('true_crime');
    expect(catalog.styleTemplateCalls).toEqual(['true_crime']);

    catalog.deferTemplates();
    const loading = store.setTopic('ghost');
    expect(catalog.styleTemplateCalls).toEqual(['true_crime', 'ghost']);
    expect(store.loadingTemplates()).toBe(true);
    expect(store.templates().map((template) => template.id)).toEqual(TRUE_CRIME_TEMPLATE_IDS);
    catalog.resolveNextTemplate();
    await loading;
    expect(store.loadingTemplates()).toBe(false);
    expect(store.templates().map((template) => template.id)).toEqual(FICTION_TEMPLATE_IDS);
  });

  it('Create_SendsCreateSeriesInput', async () => {
    const { store, seriesRepo } = setup();
    await store.init('create', null);
    await reachStep4(store);
    store.setStyleTemplate('t-dark-folklore-ink');
    expect(store.step()).toBe(4);

    const outcome = await store.submit();
    expect(outcome).toEqual({ kind: 'created', id: 's-created-5' });
    expect(seriesRepo.createCalls).toEqual([
      {
        title: 'Chuyện ma đêm khuya',
        topic: 'ghost',
        language: 'vi',
        aspect: 'landscape',
        voiceId: 'vi-quiet',
        styleTemplateId: 't-dark-folklore-ink',
        storyPreset: null,
      },
    ]);
    expect(store.seriesId()).toBe('s-created-5');
    expect(store.isDirty()).toBe(false);
    expect(store.saving()).toBe(false);
    expect(store.error()).toBeNull();
  });

  it('Edit_LoadsEntity_AndKeepsEtag', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    expect(seriesRepo.getCalls).toEqual(['s-ghost']);
    expect(store.form.getRawValue()).toEqual({
      title: 'Chuyện ma đêm khuya',
      topic: 'ghost',
      language: 'vi',
      aspect: 'landscape',
      voiceId: 'vi-quiet',
      styleTemplateId: 't-dark-folklore-ink',
    });
    expect(store.loadingEntity()).toBe(false);
    expect(store.notFound()).toBe(false);
    expect(store.archived()).toBe(false);
    expect(store.isDirty()).toBe(false);
    expect(store.stepValidity()).toEqual({ 1: true, 2: true, 3: true, 4: true });

    store.form.controls.title.setValue('Đổi tên');
    expect(await store.submit()).toEqual({ kind: 'saved' });
    expect(seriesRepo.patchCalls).toEqual([{ id: 's-ghost', patch: { title: 'Đổi tên' }, etag: '"se-41"' }]);
    expect(seriesRepo.stored('s-ghost')?.title).toBe('Đổi tên');
  });

  it('Edit_LanguageAlwaysDisabled_TopicDisabledWhileAutopilot', async () => {
    const { store } = setup();
    await store.init('edit', 's-ghost');
    expect(store.form.controls.language.disabled).toBe(true);
    expect(store.form.controls.topic.disabled).toBe(false);
    expect(store.autopilotEnabled()).toBe(false);

    await store.init('edit', 's-spiritual');
    expect(store.autopilotEnabled()).toBe(true);
    expect(store.form.controls.language.disabled).toBe(true);
    expect(store.form.controls.topic.disabled).toBe(true);
    expect(store.form.controls.language.value).toBe('vi');
  });

  it('Edit_TopicLockedByAutopilot_IgnoresSetTopic', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-spiritual');
    await store.setTopic('true_crime');
    expect(store.form.controls.topic.value).toBe('spiritual');
    expect(seriesRepo.patchCalls).toEqual([]);
  });

  it('Edit_StaleIfMatch_SetsConflict_AndKeepsValues', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.advanceVersion('s-ghost');

    const outcome = await store.submit();
    expect(outcome).toEqual({ kind: 'conflict' });
    expect(store.conflict()).toBe(true);
    expect(store.form.controls.title.value).toBe('Đổi tên');
    expect(store.saving()).toBe(false);
    expect(seriesRepo.stored('s-ghost')?.title).toBe('Chuyện ma đêm khuya');
  });

  it('Edit_SaveMineAsNewVersion_ReReadsThenPatches', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.advanceVersion('s-ghost');
    expect(await store.submit()).toEqual({ kind: 'conflict' });

    const outcome = await store.resolveConflict('saveAsNew');
    expect(seriesRepo.getCalls).toEqual(['s-ghost', 's-ghost']);
    expect(seriesRepo.patchCalls.map((call) => call.etag)).toEqual(['"se-41"', '"se-42"']);
    expect(seriesRepo.patchCalls[1]?.patch).toEqual({ title: 'Đổi tên' });
    expect(outcome).toEqual({ kind: 'saved' });
    expect(store.conflict()).toBe(false);
    expect(store.form.controls.title.value).toBe('Đổi tên');
    expect(seriesRepo.stored('s-ghost')?.title).toBe('Đổi tên');
  });

  it('Edit_ConflictReloadLatest_AdoptsServerState', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.advanceVersion('s-ghost');
    expect(await store.submit()).toEqual({ kind: 'conflict' });

    expect(await store.resolveConflict('reload')).toEqual({ kind: 'reloaded' });
    expect(store.form.controls.title.value).toBe('Chuyện ma đêm khuya');
    expect(store.conflict()).toBe(false);
    expect(store.isDirty()).toBe(false);
  });

  it('Edit_ConflictCancel_KeepsValuesAndStaysDirty', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.advanceVersion('s-ghost');
    expect(await store.submit()).toEqual({ kind: 'conflict' });

    expect(await store.resolveConflict('cancel')).toBeNull();
    expect(store.conflict()).toBe(false);
    expect(store.form.controls.title.value).toBe('Đổi tên');
    expect(seriesRepo.patchCalls.length).toBe(1);
  });

  it('Patch_Archived_SetsArchivedError', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-archived');
    expect(store.archived()).toBe(true);
    expect(store.canSave()).toBe(false);
    store.form.controls.title.setValue('Đổi tên');

    const outcome = await store.submit();
    expect(outcome).toEqual({ kind: 'failed' });
    expect(store.error()?.code).toBe('series_archived');
    expect(store.form.controls.title.value).toBe('Đổi tên');
    expect(seriesRepo.stored('s-archived')?.title).toBe('Loạt bài cũ');
  });

  it('Edit_TopicLockedError_RevertsTopic', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.failNext(appError('series_topic_locked', 409, 'precondition', { fieldErrors: { topic: ['errors.topic'] } }));

    expect(await store.submit()).toEqual({ kind: 'failed' });
    expect(store.form.controls.topic.value).toBe('ghost');
    expect(store.error()?.code).toBe('series_topic_locked');
  });

  it('Edit_EmptyDiff_IssuesNoRequest', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    expect(await store.submit()).toEqual({ kind: 'saved' });
    expect(seriesRepo.patchCalls).toEqual([]);
    expect(store.error()).toBeNull();
  });

  it('IsDirty_Debounced_150ms', async () => {
    const { store } = setup();
    // The fake clock must be installed before anything emits into the store's `debounceTime`: the operator keeps the
    // one action it scheduled and only reschedules on a later emission once that action has fired, so an action
    // scheduled on the real clock during `init()` would never be reached by `advanceTimersByTime`.
    vi.useFakeTimers();
    try {
      await store.init('create', null);
      expect(store.isDirty()).toBe(false);
      store.form.controls.title.setValue('Chuyện ma');
      expect(store.isDirty()).toBe(false);
      vi.advanceTimersByTime(DIRTY_DEBOUNCE_MS - 1);
      expect(store.isDirty()).toBe(false);
      vi.advanceTimersByTime(1);
      expect(store.isDirty()).toBe(true);
      store.form.controls.title.setValue('');
      vi.advanceTimersByTime(DIRTY_DEBOUNCE_MS);
      expect(store.isDirty()).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it('Language_IsNotSentInPatch', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    expect(await store.submit()).toEqual({ kind: 'saved' });
    const call = seriesRepo.patchCalls[0];
    expect(call?.patch).toEqual({ title: 'Đổi tên' });
    expect(Object.hasOwn(call?.patch ?? {}, 'language')).toBe(false);
    expect(Object.hasOwn(call?.patch ?? {}, 'storyPreset')).toBe(false);
  });

  it('Submit_ValidationFailed_BindsFieldErrorsAndJumpsToTheStep', async () => {
    const { store, seriesRepo, catalog } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.failNext(
      appError('validation_failed', 400, 'validation', { fieldErrors: { styleTemplateId: ['errors.style'] } }),
    );

    const outcome = await store.submit();
    expect(outcome).toEqual({ kind: 'invalid', step: 4 });
    expect(store.step()).toBe(4);
    expect(store.form.controls.styleTemplateId.hasError('server')).toBe(true);
    // Touched is what lets the mat-error render without waiting for a blur.
    expect(store.form.controls.styleTemplateId.touched).toBe(true);
    expect(store.focusRequest()?.control).toBe('styleTemplateId');
    expect(store.error()).toBeNull();
    // The jump entered step 4 for real: its catalog is read (ghost templates now loaded).
    expect(catalog.styleTemplateCalls).toEqual(['ghost']);
    expect(store.templates().length).toBeGreaterThan(0);
  });

  it('Submit_ValidationFailed_UnmappedPointer_SetsFormError', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.failNext(appError('validation_failed', 400, 'validation', { fieldErrors: { unknownField: ['errors.x'] } }));

    expect(await store.submit()).toEqual({ kind: 'invalid', step: 1 });
    expect(store.error()?.code).toBe('validation_failed');
  });

  it('Submit_VoiceLanguageMismatch_ClearsVoiceAndOpensStep3', async () => {
    const { store, seriesRepo, catalog } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.failNext(appError('voice_language_mismatch', 400, 'validation'));

    expect(await store.submit()).toEqual({ kind: 'failed' });
    expect(store.form.controls.voiceId.value).toBeNull();
    expect(store.step()).toBe(3);
    expect(store.focusRequest()?.control).toBe('voiceId');
    expect(store.error()?.code).toBe('voice_language_mismatch');
    expect(catalog.voicedCalls).toEqual(['vi']);
  });

  it('Submit_InvalidForm_JumpsToFirstInvalidStep', async () => {
    const { store, seriesRepo } = setup();
    await store.init('create', null);
    const outcome = await store.submit();
    expect(outcome).toEqual({ kind: 'invalid', step: 1 });
    expect(store.step()).toBe(1);
    expect(store.focusRequest()?.control).toBe('title');
    expect(seriesRepo.createCalls).toEqual([]);
  });

  it('Submit_InvalidForm_MarksEveryControlTouched_AndLoadsTheJumpedStepsCatalog', async () => {
    const { store, seriesRepo, catalog } = setup();
    await store.init('create', null);
    store.form.controls.title.setValue('Chuyện ma');

    // Steps 3 and 4 are unsatisfied: the submit lands on step 3 — through the same path as Next — and its voices
    // load, while every control is marked touched so the error text renders immediately.
    expect(await store.submit()).toEqual({ kind: 'invalid', step: 3 });
    expect(store.step()).toBe(3);
    expect(catalog.voicedCalls).toEqual(['vi']);
    expect(store.form.controls.title.touched).toBe(true);
    expect(store.form.controls.voiceId.touched).toBe(true);
    expect(store.form.controls.styleTemplateId.touched).toBe(true);
    expect(seriesRepo.createCalls).toEqual([]);
  });

  it('Catalogs_AreNotReRequestedOnStepReEntry_ButErrorsAreRetried', async () => {
    const { store, catalog } = setup();
    await store.init('create', null);
    fillStep1(store);
    await store.goTo(3);
    store.setVoice('vi-quiet');
    await store.goTo(4);
    expect(catalog.voicedCalls).toEqual(['vi']);
    expect(catalog.styleTemplateCalls).toEqual(['ghost']);

    // Back/Next cycles over already-loaded steps issue no further request.
    for (let i = 0; i < 2; i++) {
      store.goBack();
      store.goBack();
      expect(store.step()).toBe(2);
      await store.goTo(4);
    }
    expect(catalog.voicedCalls).toEqual(['vi']);
    expect(catalog.styleTemplateCalls).toEqual(['ghost']);

    // A failed read counts as not loaded: the next entry is the retry.
    catalog.failWith(appError('network_error', 0, 'network'));
    await store.setTopic('spiritual');
    expect(catalog.styleTemplateCalls).toEqual(['ghost', 'spiritual']);
    catalog.failWith(null);
    store.goBack();
    await store.goTo(4);
    expect(catalog.styleTemplateCalls).toEqual(['ghost', 'spiritual', 'spiritual']);
    expect(store.templates().length).toBeGreaterThan(0);
  });

  it('SetTopic_AnEagerLoadCountsAsLoaded_TheStepEntryDoesNotRepeatIt', async () => {
    const { store, catalog } = setup();
    await store.init('create', null);
    fillStep1(store);

    // One request for the topic change; entering step 4 afterwards does not ask again for the same topic.
    await store.setTopic('spiritual');
    expect(catalog.styleTemplateCalls).toEqual(['spiritual']);
    store.setVoice('vi-quiet');
    await store.goTo(4);
    expect(catalog.styleTemplateCalls).toEqual(['spiritual']);
  });

  it('Edit_SaveMineAsNewVersion_SendsOnlyTheUsersOwnEditsWithTheFreshEtag', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    // Another writer changed the aspect before the conflict re-read serves the fresh row.
    seriesRepo.willServe(CANNED_SERIES.map((row) => (row.id === 's-ghost' ? { ...row, aspect: 'portrait', etag: '"se-42"' } : row)));

    expect(await store.submit()).toEqual({ kind: 'conflict' });
    expect(await store.resolveConflict('saveAsNew')).toEqual({ kind: 'saved' });
    // The patch is the user's own edit only — never an aspect member reverting the other writer's change.
    expect(seriesRepo.patchCalls[1]?.patch).toEqual({ title: 'Đổi tên' });
    expect(seriesRepo.patchCalls[1]?.etag).toBe('"se-42"');
    expect(seriesRepo.stored('s-ghost')?.aspect).toBe('portrait');
  });

  it('Edit_SaveMineAsNewVersion_ReturnsConflictAgain_WhenTheRetryIsRefused', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.advanceVersion('s-ghost');
    expect(await store.submit()).toEqual({ kind: 'conflict' });

    // A third write lands between the re-read and the retry: the second PATCH is refused too, so the resolution
    // is another conflict (the page reopens the dialog), never a dropped outcome. The fake computes every result
    // when the call is made and only holds its delivery — the third write therefore happens between releasing
    // the re-read and the microtask that issues the PATCH.
    seriesRepo.defer();
    const resolving = store.resolveConflict('saveAsNew');
    seriesRepo.resolveNext(); // releases the re-read (its value was computed against the se-42 row)
    seriesRepo.advanceVersion('s-ghost'); // the third write
    await flush(); // the PATCH call is issued — stale the moment the fake computes it
    seriesRepo.resolveAll();
    expect(await resolving).toEqual({ kind: 'conflict' });
    expect(store.conflict()).toBe(true);
    expect(store.form.controls.title.value).toBe('Đổi tên');
  });

  it('Edit_SaveMineAsNewVersion_RepeatedRefusals_BothPatchesCarryOnlyTheUsersEdits', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.willServe(CANNED_SERIES.map((row) => (row.id === 's-ghost' ? { ...row, aspect: 'portrait', etag: '"se-42"' } : row)));
    expect(await store.submit()).toEqual({ kind: 'conflict' });

    seriesRepo.defer();
    const firstRetry = store.resolveConflict('saveAsNew');
    seriesRepo.resolveNext();
    // Another writer changes the aspect again before the first retry PATCH is issued.
    seriesRepo.willServe(CANNED_SERIES.map((row) => (row.id === 's-ghost' ? { ...row, aspect: 'landscape', etag: '"se-43"' } : row)));
    await flush();
    seriesRepo.resolveAll();
    expect(await firstRetry).toEqual({ kind: 'conflict' });

    const secondRetry = store.resolveConflict('saveAsNew');
    seriesRepo.resolveNext();
    seriesRepo.advanceVersion('s-ghost');
    await flush();
    seriesRepo.resolveAll();
    expect(await secondRetry).toEqual({ kind: 'conflict' });
    expect(seriesRepo.getCalls).toEqual(['s-ghost', 's-ghost', 's-ghost']);
    expect(seriesRepo.patchCalls).toEqual([
      { id: 's-ghost', patch: { title: 'Đổi tên' }, etag: '"se-41"' },
      { id: 's-ghost', patch: { title: 'Đổi tên' }, etag: '"se-42"' },
      { id: 's-ghost', patch: { title: 'Đổi tên' }, etag: '"se-43"' },
    ]);
    expect(store.conflict()).toBe(true);
    expect(store.form.controls.title.value).toBe('Đổi tên');
  });

  it('Edit_SaveMineRefusedAgain_CancelThenOrdinarySubmit_SendsOnlyTheUsersEdit', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.willServe(CANNED_SERIES.map((row) => (row.id === 's-ghost' ? { ...row, aspect: 'portrait', etag: '"se-42"' } : row)));
    expect(await store.submit()).toEqual({ kind: 'conflict' });

    seriesRepo.defer();
    const retry = store.resolveConflict('saveAsNew');
    seriesRepo.resolveNext();
    seriesRepo.advanceVersion('s-ghost');
    await flush();
    seriesRepo.resolveAll();
    expect(await retry).toEqual({ kind: 'conflict' });

    // Cancel keeps the typed values; the next ordinary Submit must still diff against the row the user edited,
    // not against the fresh row the re-read stored: the unchanged `aspect` must not overwrite the other writer.
    seriesRepo.defer(false);
    expect(await store.resolveConflict('cancel')).toBeNull();
    const before = seriesRepo.patchCalls.length;
    // The stored tag is the se-42 one the re-read returned while the server already sits at se-43, so this PATCH is refused
    // again; what matters here is the body it sends.
    expect(await store.submit()).toEqual({ kind: 'conflict' });
    expect(seriesRepo.patchCalls.length).toBe(before + 1);
    const last = seriesRepo.patchCalls[seriesRepo.patchCalls.length - 1];
    expect(last?.patch).toEqual({ title: 'Đổi tên' });
    expect(last?.etag).toBe('"se-42"');
  });

  it('Submit_SingleFlight_IssuesOneRequest', async () => {
    const { store, seriesRepo } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.defer();
    const first = store.submit();
    const second = store.submit();
    expect(second).toBe(first);
    expect(seriesRepo.patchCalls.length).toBe(1);
    expect(store.saving()).toBe(true);
    seriesRepo.resolveAll();
    expect(await first).toEqual({ kind: 'saved' });
    expect(store.saving()).toBe(false);
  });

  it('Submit_LateResponseAfterSessionChange_IsDiscarded', async () => {
    const { store, seriesRepo, session } = setup();
    await store.init('edit', 's-ghost');
    store.form.controls.title.setValue('Đổi tên');
    seriesRepo.defer();
    const submitting = store.submit();
    session.reset();
    seriesRepo.resolveAll();
    // The reply belongs to a session that ended: it is reported as failed and no signal of the editor adopts it.
    expect(await submitting).toEqual({ kind: 'failed' });
    expect(store.saving()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.conflict()).toBe(false);
    expect(store.seriesId()).toBe('s-ghost');
  });

  it('Init_EditNotFound_SetsNotFound', async () => {
    const { store } = setup();
    await store.init('edit', 'missing');
    expect(store.notFound()).toBe(true);
    expect(store.error()).toBeNull();
    expect(store.loadingEntity()).toBe(false);
  });

  it('Init_EditNetworkError_SetsError', async () => {
    const { store, seriesRepo } = setup();
    seriesRepo.failNext(appError('network_error', 0, 'network'));
    await store.init('edit', 's-ghost');
    expect(store.error()?.code).toBe('network_error');
    expect(store.notFound()).toBe(false);
    expect(store.loadingEntity()).toBe(false);
  });

  it('Init_LateEntityResponse_IsDiscarded', async () => {
    const { store, seriesRepo } = setup();
    seriesRepo.defer();
    const first = store.init('edit', 's-ghost');
    seriesRepo.defer(false);
    await store.init('edit', 's-spiritual');
    seriesRepo.resolveAll();
    await first;
    expect(store.seriesId()).toBe('s-spiritual');
    expect(store.form.controls.title.value).toBe('Đền chùa kể chuyện');
    expect(store.form.controls.topic.value).toBe('spiritual');
  });

  it('Edit_InactiveTemplate_AppendedAndFlagged', async () => {
    const { store, seriesRepo } = setup();
    seriesRepo.willServe([{ ...cannedRow('s-ghost'), styleTemplateId: 't-removed' }]);
    await store.init('edit', 's-ghost');
    expect(store.inactiveTemplateId()).toBe('t-removed');

    await store.goTo(4);
    const inactive = store.templates().find((template) => template.id === 't-removed');
    expect(inactive).toEqual({ id: 't-removed', name: '', topicHint: null, previewUrl: null });
    expect(store.templates().map((template) => template.id)).toEqual([...FICTION_TEMPLATE_IDS, 't-removed']);
    expect(store.inactiveTemplateId()).toBe('t-removed');
  });

  it('Edit_ActiveStoredTemplate_IsNotFlaggedInactive', async () => {
    const { store } = setup();
    await store.init('edit', 's-ghost');
    await store.goTo(4);
    expect(store.inactiveTemplateId()).toBeNull();
    expect(store.templates().map((template) => template.id)).toEqual(FICTION_TEMPLATE_IDS);
  });
});
