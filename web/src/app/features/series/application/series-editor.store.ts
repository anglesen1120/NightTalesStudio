import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DestroyRef, Injectable, computed, inject, signal, type Signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoService } from '@jsverse/transloco';
import { debounceTime, firstValueFrom, merge, type Observable } from 'rxjs';
import { bindFieldErrors, clearServerErrors, type AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import { isTrueCrime, type Aspect, type CreateSeriesInput, type Series, type Topic } from '../domain/series.model';
import { STYLE_CLEARED_BY_TOPIC_KEY, type StyleTemplate } from '../domain/style-template.model';
import { isTopicCompatible } from '../domain/topic-style-compatibility';
import type { Language } from '../domain/voice.model';
import { CatalogRepository } from './catalog.repository';
import { createSeriesForm, type SeriesForm, type SeriesFormValue } from './series-form';
import { SeriesRepository } from './series.repository';
import { VoiceStepStore } from './voice-step.store';

/** The two modes of the one editor page (`/series/new` and `/series/:id/edit`). */
export type SeriesMode = 'create' | 'edit';

/** The four wizard steps: 1 topic+language, 2 aspect, 3 voice, 4 style (plan §4 U4). */
export type SeriesStep = 1 | 2 | 3 | 4;

/** The controls a step owns, in focus order; the page moves focus to the first invalid one. */
export type SeriesStepControl = 'title' | 'topic' | 'language' | 'aspect' | 'voiceId' | 'styleTemplateId';

/** What a submit (or a conflict resolution) did; the page navigates or opens the dialog from this. */
export type SubmitOutcome =
  | { readonly kind: 'created'; readonly id: string }
  | { readonly kind: 'saved' }
  /** The form adopted the server state after `reload`: the editor stays open, the values are replaced. */
  | { readonly kind: 'reloaded' }
  | { readonly kind: 'conflict' }
  | { readonly kind: 'invalid'; readonly step: SeriesStep }
  | { readonly kind: 'failed' };

/** The three answers of `ConflictDialog` (plan §4 U7). */
export type ConflictChoice = 'reload' | 'saveAsNew' | 'cancel';

/** The dirty-indicator debounce (README D18: `isDirty` is evaluated on change, 150 ms later). */
export const DIRTY_DEBOUNCE_MS = 150;

const SERIES_STEPS = [1, 2, 3, 4] as const satisfies readonly SeriesStep[];

const STEP_CONTROLS: Readonly<Record<SeriesStep, readonly SeriesStepControl[]>> = {
  1: ['title', 'topic', 'language'],
  2: ['aspect'],
  3: ['voiceId'],
  4: ['styleTemplateId'],
};

const NEXT_STEP: Readonly<Record<SeriesStep, SeriesStep>> = { 1: 2, 2: 3, 3: 4, 4: 4 };
const PREVIOUS_STEP: Readonly<Record<SeriesStep, SeriesStep>> = { 1: 1, 2: 1, 3: 2, 4: 3 };

/** A patch being assembled member by member (`SeriesPatchInput` is readonly). */
interface MutableSeriesPatch {
  title?: string;
  topic?: Topic;
  aspect?: Aspect;
  voiceId?: string;
  styleTemplateId?: string;
}

interface CachedTemplates {
  readonly etag: string | null;
  readonly templates: readonly StyleTemplate[];
}

/**
 * State of the 4-step series editor (`/series/new` and `/series/:id/edit`) — README D2: application layer, page-scoped
 * `@Injectable()` provided together with `VoiceStepStore` by the editor page, never `providedIn: 'root'`.
 *
 * The store owns the typed form (`series-form.ts`), so every rule of the wizard lives in one place:
 *
 * - **Steps.** `stepValidity`/`canGoNext`/`firstInvalidStep` are `computed` over the form's own validity; `goTo` only
 *   reaches a step up to the first invalid one, and entering step 3 or 4 triggers that step's one catalog request.
 * - **U6 (language).** `setLanguage` clears the voice (the injected `VoiceStepStore` announces
 *   `series.voice.clearedByLanguage`) and re-checks the style, which is cleared with `series.style.clearedByTopic`
 *   when the current topic cannot use it. `setTopic` does the same style check.
 * - **Submit.** One in-flight submit (a second call returns the same promise, so no second request); `create` posts a
 *   `CreateSeriesInput` (title trimmed), `edit` sends only the members that differ from the loaded entity — never
 *   `language` — with `If-Match` and never a fabricated entity. `412`/409 `concurrency_conflict` raise `conflict` and
 *   keep every entered value; a 412 is never retried automatically.
 * - **Late results.** Every request captures the session generation (and the entity request id); a reply that arrives
 *   after the session ended or after another `init()` is discarded without touching a signal.
 */
@Injectable()
export class SeriesEditorStore {
  private readonly repository = inject(SeriesRepository);
  private readonly catalog = inject(CatalogRepository);
  private readonly voiceStep = inject(VoiceStepStore);
  private readonly session = inject(SessionStore);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly transloco = inject(TranslocoService);
  private readonly destroyRef = inject(DestroyRef);

  readonly form: SeriesForm = createSeriesForm();

  /** Every form value/status/disabled event: the bridge that keeps the `computed` state below current (D13). */
  private readonly formEvents$: Observable<unknown> = merge(this.form.events, this.form.valueChanges, this.form.statusChanges);
  private readonly formRevision = signal(0);
  private readonly baseline = signal(this.serialize());
  private readonly dirtyJson = signal(this.serialize());

  private readonly templateCache = new Map<Topic, CachedTemplates>();
  private templateSeq = 0;
  private entityRequest = 0;

  /**
   * The `topicHint` of every template id the store has seen — remembered by id so the compatibility check works
   * while the catalog is not loaded yet (edit before step 4) or no longer holds the old template (QA-2). The
   * stored style is seeded in `applyEntity` from the series topic: a `true_crime` series had to pick a `true_crime`
   * template, any other topic one that is not (`StyleTopicPolicy`). Compatibility reads this hint like a `null`
   * hint: `null` stands in for "anything but `true_crime`" for the check.
   */
  private readonly styleHints = new Map<string, Topic | null>();
  private focusSeq = 0;
  private etag: string | null = null;
  private storedSeries: Series | null = null;
  /** The row the user edited before conflict re-reads replace the server merge target. */
  private editBaseline: Series | null = null;
  private inFlight: Promise<SubmitOutcome> | null = null;
  private templatesInFlight: { readonly topic: Topic; readonly promise: Promise<void> } | null = null;
  /** The topic of the list `templates()` currently shows after a successful read (`null`: nothing or an error). */
  private loadedTemplatesTopic: Topic | null = null;
  /** The language of the list `voices()` currently shows (mirrors `VoiceStepStore`'s last successful read). */
  private loadedVoicesLanguage: Language | null = null;

  readonly mode = signal<SeriesMode>('create');
  readonly seriesId = signal<string | null>(null);
  readonly step = signal<SeriesStep>(1);
  readonly loadingEntity = signal(false);
  readonly notFound = signal(false);
  readonly archived = signal(false);
  readonly autopilotEnabled = signal(false);
  readonly templates = signal<readonly StyleTemplate[]>([]);
  /** The stored template a series still references although it is no longer in the catalog (edit mode only). */
  readonly inactiveTemplateId = signal<string | null>(null);
  readonly loadingTemplates = signal(false);
  readonly templatesError = signal<AppError | null>(null);
  readonly saving = signal(false);
  readonly error = signal<AppError | null>(null);
  readonly conflict = signal(false);
  /** The code of the refusal behind `conflict` (`series_conflict` / `concurrency_conflict`) — the dialog's copy. */
  readonly conflictCode = signal<string | null>(null);
  /** The `traceId` of that refusal (09 README D15), for the dialog's details row; `null` when the body had none. */
  readonly conflictTraceId = signal<string | null>(null);
  readonly focusRequest = signal<{ readonly control: SeriesStepControl; readonly seq: number } | null>(null);

  /** The form's value, re-read on every form event: what the page's template bridge renders (D13). */
  readonly value = computed<SeriesFormValue>(() => {
    this.formRevision();
    return this.form.getRawValue();
  });

  /** The last server state the editor compares against (the JSON the dirty check and the patch diff use). */
  readonly baselineJson: Signal<string> = this.baseline.asReadonly();

  /** Dirty *indicator* only, 150 ms debounced (D18). Submitting does not consult it — it reads the live form. */
  readonly isDirty = computed(() => this.dirtyJson() !== this.baseline());

  /**
   * Whether each step's controls are satisfied. The question is `invalid`, never `valid`: a control the editor
   * disables (`language` always in edit mode, `topic` while autopilot is on) has status `DISABLED`, so `valid` is
   * false for it and its step could never pass — the same rule Angular applies to the form as a whole.
   */
  readonly stepValidity = computed<Readonly<Record<SeriesStep, boolean>>>(() => {
    this.formRevision();
    const controls = this.form.controls;
    return {
      1: !controls.title.invalid && !controls.topic.invalid && !controls.language.invalid,
      2: !controls.aspect.invalid,
      3: !controls.voiceId.invalid,
      4: !controls.styleTemplateId.invalid,
    };
  });

  readonly canGoNext = computed(() => this.stepValidity()[this.step()]);
  readonly firstInvalidStep = computed<SeriesStep | null>(() => {
    const validity = this.stepValidity();
    for (const step of SERIES_STEPS) {
      if (!validity[step]) return step;
    }
    return null;
  });

  /** Save is unavailable while a save is in flight and for an archived series (it cannot be un-archived in P). */
  readonly canSave = computed(() => !this.saving() && !this.archived());

  /** The voice catalog lives in `VoiceStepStore`; these are pass-throughs so the page reads one store per step. */
  readonly voices = computed(() => this.voiceStep.voices());
  readonly loadingVoices = computed(() => this.voiceStep.loading());
  readonly voicesError = computed(() => this.voiceStep.error());
  readonly voiceRetryAfterSec = computed(() => this.voiceStep.retryAfterSec());
  readonly templatesRetryAfterSec = computed<number | null>(() => {
    const error = this.templatesError();
    return error !== null && error.kind === 'rate_limit' ? (error.retryAfterSec ?? null) : null;
  });

  constructor() {
    this.formEvents$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.formRevision.update((revision) => revision + 1);
    });
    this.formEvents$.pipe(debounceTime(DIRTY_DEBOUNCE_MS), takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.dirtyJson.set(this.serialize());
    });
  }

  /**
   * Prepares the editor. `create` resets to the deck's defaults; `edit` loads the series, keeps its ETag privately,
   * pre-fills every control and disables what may not change (`language` always, `topic` while autopilot is on).
   */
  async init(mode: SeriesMode, id: string | null): Promise<void> {
    this.mode.set(mode);
    this.seriesId.set(id);
    this.step.set(1);
    this.error.set(null);
    this.conflict.set(false);
    this.conflictCode.set(null);
    this.conflictTraceId.set(null);
    this.notFound.set(false);
    this.archived.set(false);
    this.autopilotEnabled.set(false);
    this.templates.set([]);
    this.templatesError.set(null);
    this.inactiveTemplateId.set(null);
    this.templateCache.clear();
    this.loadedTemplatesTopic = null;
    this.loadedVoicesLanguage = null;
    this.templatesInFlight = null;
    this.styleHints.clear();
    this.templateSeq += 1;
    this.etag = null;
    this.storedSeries = null;
    this.editBaseline = null;
    this.voiceStep.clear();
    this.restoreDefaults();
    if (mode === 'create') {
      this.markClean();
      return;
    }
    if (id === null) {
      this.notFound.set(true);
      return;
    }
    const requestId = ++this.entityRequest;
    const sessionGeneration = this.session.generation();
    this.loadingEntity.set(true);
    const result = await this.repository.get(id);
    if (requestId !== this.entityRequest || id !== this.seriesId() || sessionGeneration !== this.session.generation()) return;
    this.loadingEntity.set(false);
    if (!result.ok) {
      if (result.error.status === 404 || result.error.code === 'not_found') {
        this.notFound.set(true);
        return;
      }
      this.error.set(result.error);
      return;
    }
    this.applyEntity(result.value);
  }

  /** Next is a no-op on an invalid step (the page also disables the button) and on the last one. */
  async goNext(): Promise<void> {
    const step = this.step();
    if (step === 4 || !this.stepValidity()[step]) return;
    await this.goToStep(NEXT_STEP[step]);
  }

  goBack(): void {
    void this.goToStep(PREVIOUS_STEP[this.step()]);
  }

  /** Jumps to a step the user may already reach: never past the first invalid one. */
  async goTo(step: SeriesStep): Promise<void> {
    const firstInvalid = this.firstInvalidStep();
    if (firstInvalid !== null && step > firstInvalid) return;
    await this.goToStep(step);
  }

  /**
   * The one way a step changes: sets the current step, then runs that step's enter effects (its catalog read).
   * Programmatic jumps — an invalid submit or a server `validation_failed` — take this same path, so landing on
   * step 3 or 4 loads voices or templates exactly like a user click.
   */
  private async goToStep(step: SeriesStep): Promise<void> {
    this.step.set(step);
    await this.enterStep(step);
  }

  /** Whether a step's controls are all valid; the map itself is `stepValidity`. */
  stepValid(step: SeriesStep): boolean {
    return this.stepValidity()[step];
  }

  /**
   * Sets the topic and keeps the style honest: the selected template is cleared with `series.style.clearedByTopic`
   * when the new topic cannot use it, and the step-4 catalog is re-read (one request per change, the current list
   * stays visible while it loads). Ignored while the control is locked by autopilot.
   */
  async setTopic(topic: Topic): Promise<void> {
    if (this.form.controls.topic.disabled || this.form.controls.topic.value === topic) return;
    this.form.controls.topic.setValue(topic);
    if (this.clearIncompatibleStyle()) void this.announce(STYLE_CLEARED_BY_TOPIC_KEY);
    await this.loadTemplates();
  }

  /**
   * Sets the language (immutable server-side, so the control is disabled in edit mode) and applies U6: the voice is
   * cleared — `VoiceStepStore.load` announces `series.voice.clearedByLanguage` and drops its selection — and the style
   * is cleared when the current topic cannot use it.
   */
  async setLanguage(language: Language): Promise<void> {
    if (this.form.controls.language.disabled || this.form.controls.language.value === language) return;
    this.form.controls.language.setValue(language);
    this.form.controls.voiceId.setValue(null);
    if (this.clearIncompatibleStyle()) void this.announce(STYLE_CLEARED_BY_TOPIC_KEY);
    await this.loadVoices();
  }

  setAspect(aspect: Aspect): void {
    this.form.controls.aspect.setValue(aspect);
  }

  /** Records the picked voice in the form and in the voice step, so the picker and the payload cannot drift. */
  setVoice(id: string): void {
    this.form.controls.voiceId.setValue(id);
    this.voiceStep.select(id);
  }

  /** Records the picked card in the form and remembers its `topicHint`, so a later topic change can judge it. */
  setStyleTemplate(id: string): void {
    this.form.controls.styleTemplateId.setValue(id);
    const hint = this.templates().find((template) => template.id === id)?.topicHint;
    if (hint !== undefined) this.styleHints.set(id, hint);
  }

  /**
   * Submits once. A second call while the first is in flight returns the same promise (one request, one in-flight
   * intent); an invalid form jumps to the first invalid step and asks the page to focus its first control.
   */
  submit(): Promise<SubmitOutcome> {
    if (this.inFlight !== null) return this.inFlight;
    const request = this.runSubmit().finally(() => {
      if (this.inFlight === request) this.inFlight = null;
    });
    this.inFlight = request;
    return request;
  }

  /** The `ConflictDialog` answers: keep both (reload), re-read then save, or stay (`cancel`). */
  async resolveConflict(choice: ConflictChoice): Promise<SubmitOutcome | null> {
    if (choice === 'cancel') {
      this.conflict.set(false);
      this.conflictCode.set(null);
      this.conflictTraceId.set(null);
      return null;
    }
    const id = this.seriesId();
    if (id === null) return { kind: 'failed' };
    // "Save mine" diffs the entered values against the row the user edited, never against the fresh row the
    // re-read is about to return: another writer's changes stay untouched (the patch carries only the edits).
    this.editBaseline ??= this.storedSeries;
    const sessionGeneration = this.session.generation();
    const result = await this.repository.get(id);
    if (sessionGeneration !== this.session.generation() || id !== this.seriesId()) return { kind: 'failed' };
    if (!result.ok) {
      this.error.set(result.error);
      return { kind: 'failed' };
    }
    if (choice === 'reload') {
      this.conflict.set(false);
      this.conflictCode.set(null);
      this.conflictTraceId.set(null);
      this.applyEntity(result.value);
      return { kind: 'reloaded' };
    }
    // Save mine as new version: the server state becomes the row the committed patch merges onto and its fresh
    // tag the If-Match value; the patch itself still diffs the form against `editBaseline`, so the values
    // the user typed are the only members sent.
    this.etag = result.value.etag;
    this.storedSeries = { ...result.value };
    this.conflict.set(false);
    this.conflictCode.set(null);
    this.conflictTraceId.set(null);
    this.saving.set(true);
    try {
      return await this.saveSeries(this.editBaseline);
    } finally {
      this.saving.set(false);
    }
  }

  private async runSubmit(): Promise<SubmitOutcome> {
    const form = this.form;
    clearServerErrors(form);
    this.error.set(null);
    if (form.invalid) {
      // The errors must render without waiting for a blur: every control is touched, the jump goes through the
      // same step-change path as Next/Back, and the page focuses the step's first control.
      form.markAllAsTouched();
      const step = this.firstInvalidStep() ?? 1;
      this.requestFocus(STEP_CONTROLS[step][0] ?? 'title');
      await this.goToStep(step);
      return { kind: 'invalid', step };
    }
    this.saving.set(true);
    try {
      return this.mode() === 'create' ? await this.createSeries() : await this.saveSeries(this.editBaseline ?? this.storedSeries);
    } finally {
      this.saving.set(false);
    }
  }

  private async createSeries(): Promise<SubmitOutcome> {
    const current = this.form.getRawValue();
    const voiceId = current.voiceId;
    const styleTemplateId = current.styleTemplateId;
    if (voiceId === null || styleTemplateId === null) return { kind: 'invalid', step: voiceId === null ? 3 : 4 };
    const input: CreateSeriesInput = {
      title: current.title.trim(),
      topic: current.topic,
      language: current.language,
      aspect: current.aspect,
      voiceId,
      styleTemplateId,
      storyPreset: null,
    };
    const sessionGeneration = this.session.generation();
    const result = await this.repository.create(input);
    if (sessionGeneration !== this.session.generation()) return { kind: 'failed' };
    if (!result.ok) return this.failedOutcome(result.error);
    this.etag = result.value.etag;
    this.seriesId.set(result.value.id);
    this.markClean();
    return { kind: 'created', id: result.value.id };
  }

  /**
   * Sends the diff of `diffBaseline` (the row the user edited: the stored entity normally, the pre-conflict row
   * after "Save mine as new version" re-read the fresh one). `storedSeries` stays the merge target the committed
   * patch is folded into — after a conflict that is the server row, so another writer's fields survive.
   */
  private async saveSeries(diffBaseline: Series | null): Promise<SubmitOutcome> {
    const id = this.seriesId();
    const etag = this.etag;
    if (id === null || etag === null) {
      // No baseline tag: there is nothing to make the write conditional on, and inventing one is forbidden (D15). The
      // catalogue's own `if_match_required` (428, client_bug) is the honest code for a missing required header.
      this.error.set({ code: 'if_match_required', status: 428, kind: 'client_bug', fieldErrors: {} });
      return { kind: 'failed' };
    }
    const patch = this.buildPatch(diffBaseline);
    if (Object.keys(patch).length === 0) {
      this.markClean();
      return { kind: 'saved' };
    }
    const sessionGeneration = this.session.generation();
    const result = await this.repository.patch(id, patch, etag);
    if (sessionGeneration !== this.session.generation() || id !== this.seriesId()) return { kind: 'failed' };
    if (!result.ok) return this.failedOutcome(result.error);
    this.etag = result.value.etag;
    // PATCH answers only `{ etag }`, so the baseline merges the submitted members onto the stored row and never
    // fabricates the others (task §Design: "the store updates only fields from the submitted patch"). Without this
    // the next save would diff against the pre-patch row: reverting to the original value sent nothing, a second
    // save re-sent every field, and `series_topic_locked` reverted to the pre-patch topic (QA-1).
    if (this.storedSeries !== null) {
      const merged: Series = { ...this.storedSeries, ...patch, etag: result.value.etag };
      this.storedSeries = merged;
      // Reconcile the inactive-template flag when the stored style could have changed hands.
      if (patch.styleTemplateId !== undefined || this.inactiveTemplateId() !== null) this.applyInactiveTemplate(merged.topic);
    }
    this.markClean();
    return { kind: 'saved' };
  }

  /** Only the members that differ from `stored`; `language` is immutable and a preset is not editable in P. */
  private buildPatch(stored: Series | null): MutableSeriesPatch {
    const patch: MutableSeriesPatch = {};
    if (stored === null) return patch;
    const current = this.form.getRawValue();
    const title = current.title.trim();
    if (title !== stored.title) patch.title = title;
    if (current.topic !== stored.topic) patch.topic = current.topic;
    if (current.aspect !== stored.aspect) patch.aspect = current.aspect;
    if (current.voiceId !== null && current.voiceId !== stored.voiceId) patch.voiceId = current.voiceId;
    if (current.styleTemplateId !== null && current.styleTemplateId !== stored.styleTemplateId) {
      patch.styleTemplateId = current.styleTemplateId;
    }
    return patch;
  }

  /** The one place a failed response becomes an outcome and a signal (task §Failure handling). */
  private async failedOutcome(error: AppError): Promise<SubmitOutcome> {
    if (error.kind === 'conflict') {
      // 412 `series_conflict` and 409 `concurrency_conflict`: the dialog decides, the entered values stay (D9).
      this.editBaseline ??= this.storedSeries;
      this.conflict.set(true);
      this.conflictCode.set(error.code);
      this.conflictTraceId.set(error.traceId ?? null);
      return { kind: 'conflict' };
    }
    switch (error.code) {
      case 'series_topic_locked': {
        // The topic the server holds wins; the hint `autopilot.topic_locked` is rendered by the page.
        if (this.storedSeries !== null) this.form.controls.topic.setValue(this.storedSeries.topic);
        this.error.set(error);
        return { kind: 'failed' };
      }
      case 'series_archived':
        this.archived.set(true);
        this.error.set(error);
        return { kind: 'failed' };
      case 'voice_language_mismatch':
        this.form.controls.voiceId.setValue(null);
        this.voiceStep.clear();
        // The shown list is stale now: the mismatch is a fresh catalog problem, so the jump fetches step 3 again
        // instead of reusing the loaded language (the same rule an explicit retry follows).
        this.loadedVoicesLanguage = null;
        this.requestFocus('voiceId');
        await this.goToStep(3);
        this.error.set(error);
        return { kind: 'failed' };
      case 'validation_failed': {
        // markTouched: the bound control's mat-error must render without waiting for a blur.
        const unmapped = bindFieldErrors(this.form, error, { markTouched: true });
        const step = this.stepForFieldErrors(error, unmapped);
        if (unmapped.length > 0) this.error.set(error);
        this.requestFocus(STEP_CONTROLS[step][0] ?? 'title');
        await this.goToStep(step);
        return { kind: 'invalid', step };
      }
      default:
        // Includes 428 `if_match_required`, whose dev-only `client_bug` line the AppErrorMapper already writes;
        // it is never auto-retried (D9).
        this.error.set(error);
        return { kind: 'failed' };
    }
  }

  /** The step a `validation_failed` lands on: the first pointer's own step (a mapped pointer names the control it
   * was bound to; an unmapped one may still match a step's control name), else the first locally invalid step. */
  private stepForFieldErrors(error: AppError, unmapped: readonly string[]): SeriesStep {
    for (const pointer of [...Object.keys(error.fieldErrors), ...unmapped]) {
      const step = this.stepOfPointer(pointer);
      if (step !== null) return step;
    }
    return this.firstInvalidStep() ?? 1;
  }

  private stepOfPointer(pointer: string | undefined): SeriesStep | null {
    switch (pointer) {
      case 'title':
      case 'topic':
      case 'language':
        return 1;
      case 'aspect':
        return 2;
      case 'voiceId':
        return 3;
      case 'styleTemplateId':
        return 4;
      default:
        return null;
    }
  }

  private requestFocus(control: SeriesStepControl): void {
    this.focusSeq += 1;
    this.focusRequest.set({ control, seq: this.focusSeq });
  }

  private async enterStep(step: SeriesStep): Promise<void> {
    if (step === 3) await this.loadVoices();
    if (step === 4) await this.loadTemplates();
  }

  /**
   * The step-3 catalog read: one request per language change (the picker keeps its last good list while an
   * `en` load is in flight). A step re-entry re-uses the list already showing for the same language — a load
   * still in flight is joined, a finished-and-shown one is not re-requested; after an error the next entry is
   * the retry.
   */
  private async loadVoices(): Promise<void> {
    const language = this.form.controls.language.value;
    const voicesLoaded = language === this.loadedVoicesLanguage && !this.voiceStep.loading() && this.voiceStep.error() === null;
    if (!voicesLoaded) {
      await this.voiceStep.load(language);
      if (
        language === this.form.controls.language.value &&
        language === this.voiceStep.language() &&
        !this.voiceStep.loading() &&
        this.voiceStep.error() === null
      ) {
        this.loadedVoicesLanguage = language;
      }
    }
    if (language !== this.form.controls.language.value) return;
    const selected = this.form.controls.voiceId.value;
    if (selected !== null) this.voiceStep.select(selected);
  }

  /**
   * The step-4 catalog read: the request goes out only when the list the editor would show is not already the
   * current topic's — an in-flight request for it is joined, a finished-and-shown one is not re-requested, and a
   * failed read counts as not loaded so the next entry is the retry.
   */
  private async loadTemplates(): Promise<void> {
    const topic = this.form.controls.topic.value;
    if (topic === this.loadedTemplatesTopic && !this.loadingTemplates() && this.templatesError() === null) return;
    if (this.templatesInFlight?.topic === topic) {
      await this.templatesInFlight.promise;
      return;
    }
    const promise = this.fetchTemplates(topic);
    this.templatesInFlight = { topic, promise };
    try {
      await promise;
    } finally {
      if (this.templatesInFlight?.promise === promise) this.templatesInFlight = null;
    }
  }

  /** One templates request for `topic`; a superseded answer is dropped and the current list stays visible. */
  private async fetchTemplates(topic: Topic): Promise<void> {
    const request = ++this.templateSeq;
    const sessionGeneration = this.session.generation();
    const cached = this.templateCache.get(topic);
    this.loadingTemplates.set(true);
    this.templatesError.set(null);
    const result = await this.catalog.styleTemplates(topic, cached?.etag ?? null);
    if (request !== this.templateSeq || topic !== this.form.controls.topic.value || sessionGeneration !== this.session.generation()) {
      if (request === this.templateSeq) this.loadingTemplates.set(false);
      return;
    }
    this.loadingTemplates.set(false);
    if (!result.ok) {
      this.templatesError.set(result.error);
      return;
    }
    if (result.value.kind === 'notModified') {
      if (cached !== undefined) this.templates.set(cached.templates);
      this.loadedTemplatesTopic = topic;
      this.finishTemplateList(topic);
      return;
    }
    const templates = result.value.templates.filter((template) => isTopicCompatible(topic, template));
    for (const template of result.value.templates) this.styleHints.set(template.id, template.topicHint);
    this.templateCache.set(topic, { etag: result.value.etag, templates });
    this.templates.set(templates);
    this.loadedTemplatesTopic = topic;
    this.finishTemplateList(topic);
  }

  /**
   * The two rules every refreshed template list applies: flag the stored-but-de-activated template for its topic,
   * then clear a selection the new list no longer shows (the stored inactive style survives only while the topic
   * is the stored one — QA-2's hidden-selection case).
   */
  private finishTemplateList(topic: Topic): void {
    this.applyInactiveTemplate(topic);
    if (this.clearStyleHiddenFromGrid()) void this.announce(STYLE_CLEARED_BY_TOPIC_KEY);
  }

  /**
   * The catalog hides a de-activated template (09.4.2-T1:147) while a series may still reference it. In edit mode the
   * stored id is kept as an extra entry flagged through `inactiveTemplateId`, and only while the topic is still the
   * stored one. Its `name` is unknown (the response never carried it), so the card carries no `series.style.<name>` key:
   * the UI renders `series.style.inactive` for the flagged entry [OPEN: fallback shape not defined upstream].
   */
  private applyInactiveTemplate(topic: Topic): void {
    const stored = this.storedSeries;
    const storedId = stored?.styleTemplateId ?? null;
    if (this.mode() !== 'edit' || stored === null || storedId === null || stored.topic !== topic) {
      // The flag belongs to the stored topic only; a synthetic leftover of a previous list goes with it.
      this.inactiveTemplateId.set(null);
      this.templates.update((list) => list.filter((template) => template.name !== ''));
      return;
    }
    if (this.templates().some((template) => template.id === storedId)) {
      this.inactiveTemplateId.set(null);
      return;
    }
    this.inactiveTemplateId.set(storedId);
    this.templates.update((templates) => [...templates, { id: storedId, name: '', topicHint: null, previewUrl: null }]);
  }

  /**
   * Clears a selection the current topic may not use; returns whether it cleared one (the caller announces). The
   * hint comes from `styleHints` — remembered when the template was loaded, picked or derived from the stored
   * series — because the catalog list is empty in edit mode before step 4 and drops the old topic's rows on a
   * topic change (QA-2). An id with no remembered hint is treated like a `null` hint: compatible with the fiction
   * topics, cleared under `true_crime`.
   */
  private clearIncompatibleStyle(): boolean {
    const selected = this.form.controls.styleTemplateId.value;
    if (selected === null) return false;
    const hint =
      this.styleHints.get(selected) ?? this.templates().find((candidate) => candidate.id === selected)?.topicHint ?? null;
    if (isTopicCompatible(this.form.controls.topic.value, { topicHint: hint })) return false;
    this.form.controls.styleTemplateId.setValue(null);
    if (this.inactiveTemplateId() === selected) this.inactiveTemplateId.set(null);
    return true;
  }

  /**
   * Clears a still-selected style the freshly served list does not carry: a de-activated stored style is only
   * shown for the topic it belongs to, so on any other topic it must not survive as a hidden form value. Returns
   * whether it cleared one (the caller announces).
   */
  private clearStyleHiddenFromGrid(): boolean {
    const selected = this.form.controls.styleTemplateId.value;
    if (selected === null) return false;
    if (this.templates().some((template) => template.id === selected)) return false;
    this.form.controls.styleTemplateId.setValue(null);
    if (this.inactiveTemplateId() === selected) this.inactiveTemplateId.set(null);
    return true;
  }

  private applyEntity(series: Series): void {
    this.etag = series.etag;
    this.storedSeries = { ...series };
    this.archived.set(series.archivedAt !== null);
    this.autopilotEnabled.set(series.autopilotEnabled);
    this.form.setValue({
      title: series.title,
      topic: series.topic,
      language: series.language,
      aspect: series.aspect,
      voiceId: series.voiceId,
      styleTemplateId: series.styleTemplateId,
    });
    this.form.controls.language.disable();
    // The stored style's hint is remembered before any catalog row is seen: a `true_crime` series could only have
    // picked a `true_crime` template, any other topic one without that hint (`StyleTopicPolicy`, QA-2).
    this.styleHints.set(series.styleTemplateId, isTrueCrime(series.topic) ? 'true_crime' : null);
    if (series.autopilotEnabled) this.form.controls.topic.disable();
    this.inactiveTemplateId.set(series.styleTemplateId);
    this.voiceStep.select(series.voiceId);
    this.markClean();
  }

  /** Back to the create-mode defaults with both immutable-in-edit controls enabled again. */
  private restoreDefaults(): void {
    this.form.controls.language.enable();
    this.form.controls.topic.enable();
    this.form.reset({ title: '', topic: 'ghost', language: 'vi', aspect: 'landscape', voiceId: null, styleTemplateId: null });
    this.form.markAsPristine();
  }

  /** The server state (or the just-submitted values) become the new baseline: the dirty indicator goes quiet. */
  private markClean(): void {
    this.editBaseline = null;
    this.error.set(null);
    this.conflict.set(false);
    this.conflictCode.set(null);
    this.conflictTraceId.set(null);
    const snapshot = this.serialize();
    this.baseline.set(snapshot);
    this.dirtyJson.set(snapshot);
  }

  private serialize(): string {
    return JSON.stringify(this.form.getRawValue());
  }

  private async announce(key: string): Promise<void> {
    const message = await firstValueFrom(this.transloco.selectTranslate<string>(key));
    await this.announcer.announce(message, 'polite');
  }
}
