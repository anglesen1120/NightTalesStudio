import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, ViewContainerRef, afterRenderEffect, computed, effect, inject, linkedSignal, untracked } from '@angular/core';
import { LiveAnnouncer } from '@angular/cdk/a11y';
import { toSignal } from '@angular/core/rxjs-interop';
import { ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoPipe, TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { combineLatest, firstValueFrom } from 'rxjs';
import { AppErrorMapper, type ServerControlError } from '@core/errors';
import { SessionStore } from '@core/session';
import { CharCounter, ConflictDialog, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader, RetryCountdown, Stepper, type StepperStep } from '@shared/ui';
import { SeriesEditorStore, type SubmitOutcome } from '../application/series-editor.store';
import { SERIES_TITLE_MAX } from '../application/series-form';
import { VoiceStepStore } from '../application/voice-step.store';
import { ASPECTS, TOPICS, aspectLabelKey, topicLabelKey, type Topic } from '../domain/series.model';
import { StyleGrid } from '../ui/style-grid';
import { VoicePicker } from '../ui/voice-picker';

const STEP_KEYS = ['series.step.topic', 'series.step.aspect', 'series.step.voice', 'series.step.style'] as const;
type ControlName = 'title' | 'topic' | 'language' | 'aspect' | 'voiceId' | 'styleTemplateId';

@Component({
  selector: 'nts-series-editor-page',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatRadioModule, MatSelectModule, TranslocoPipe, CharCounter, EmptyState, ErrorState, FormErrorAlert, LoadingState, PageHeader, RetryCountdown, Stepper, StyleGrid, VoicePicker],
  providers: [SeriesEditorStore, VoiceStepStore, provideTranslocoScope('series', 'voices', 'errors', 'autopilot')],
  templateUrl: './series-editor-page.html',
  styleUrl: './series-editor-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeriesEditorPage {
  protected readonly store = inject(SeriesEditorStore);
  protected readonly voices = inject(VoiceStepStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly conflictDialog = inject(ConflictDialog);
  private readonly transloco = inject(TranslocoService);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly snackbar = inject(MatSnackBar);
  private readonly mapper = inject(AppErrorMapper);
  private readonly routeState = toSignal(combineLatest([this.route.data, this.route.paramMap]), { initialValue: [this.route.snapshot.data, this.route.snapshot.paramMap] as const });
  /**
   * The store's reactive form value (it re-reads the form on every value/status event through `formRevision`).
   * The page must not bridge the form with its own `toSignal(merge(...))`: `toSignal` returns a `computed` whose
   * default `Object.is` equality suppresses notifications when the merged stream ends a burst on an unchanged
   * value (e.g. `statusChanges` re-emitting `INVALID`), which left the topic/language/voice inputs stale.
   */
  protected readonly value = this.store.value;
  protected readonly titleMax = SERIES_TITLE_MAX;
  protected readonly topics = TOPICS;
  protected readonly aspects = ASPECTS;
  protected readonly topicKey = topicLabelKey;
  protected readonly aspectKey = aspectLabelKey;
  protected readonly topicHints: Readonly<Record<Topic, string>> = { ghost: 'series.topicHint.ghost', spiritual: 'series.topicHint.spiritual', true_crime: 'series.topicHint.true_crime' };
  protected readonly steps = computed<readonly StepperStep[]>(() => STEP_KEYS.map((labelKey, index) => ({ labelKey, state: index + 1 === this.store.step() ? 'current' : index + 1 < this.store.step() ? 'done' : 'todo' })));
  protected readonly blocked = computed(() => this.store.saving() || this.store.archived());
  protected readonly allValid = computed(() => { this.value(); return this.store.form.valid; });
  protected readonly templatesErrorKey = computed(() => {
    const error = this.store.templatesError();
    return error === null ? null : this.mapper.messageKey(error);
  });
  protected readonly templatesRetryAfter = computed(() => {
    const error = this.store.templatesError();
    return error?.kind === 'rate_limit' ? error.retryAfterSec ?? null : null;
  });
  protected readonly templatesRetryLocked = linkedSignal({ source: this.templatesRetryAfter, computation: () => true });
  protected readonly voiceErrorKey = computed(() => {
    const error = this.voices.error();
    return error === null ? null : this.mapper.messageKey(error);
  });
  protected readonly inactiveVoiceId = computed(() => {
    const id = this.value().voiceId;
    return this.store.mode() === 'edit' && id !== null && !this.voices.loading() && this.voices.error() === null
      && !this.voices.voices().some((voice) => voice.id === id) ? id : null;
  });
  protected readonly unmappedKeys = computed(() => {
    const error = this.store.error();
    if (error === null) return [];
    const names: readonly string[] = ['title', 'topic', 'language', 'aspect', 'voiceId', 'styleTemplateId'];
    return Object.keys(error.fieldErrors).filter((key) => !names.includes(key));
  });
  /**
   * `mat-select` owns `aria-describedby` through `userAriaDescribedBy`; binding the raw attribute is
   * stripped by `MatFormField._syncDescribedByIds`, so the error/hint id goes through the input (empty = none).
   */
  protected readonly languageDescribedBy = computed(() => {
    if (this.store.mode() === 'edit') return 'series-language-locked';
    if (this.voices.fieldError() !== null || this.fieldError('language') !== null) return 'series-language-error';
    return '';
  });
  private lastStep: number | null = null;
  private lastFocusSeq = -1;
  private conflictOpen = false;

  constructor() {
    effect(() => {
      const [data, params] = this.routeState();
      const mode = data['mode'] === 'edit' ? 'edit' : 'create';
      this.lastStep = null;
      this.lastFocusSeq = -1;
      untracked(() => { void this.store.init(mode, params.get('id')); });
    });
    afterRenderEffect(() => {
      const step = this.store.step();
      const focus = this.store.focusRequest();
      const loading = this.store.loadingEntity();
      // VoicePicker's existing public API has no describedBy input. The container links its
      // field-level errors to the real radio controls without giving the presentational picker a store.
      this.voices.voices();
      const voiceError = this.fieldError('voiceId');
      for (const radio of this.host.nativeElement.querySelectorAll<HTMLInputElement>('nts-voice-picker input[type="radio"]')) {
        if (voiceError === null) {
          radio.removeAttribute('aria-describedby');
          radio.removeAttribute('aria-invalid');
        } else {
          radio.setAttribute('aria-describedby', 'series-voice-error');
          radio.setAttribute('aria-invalid', 'true');
        }
      }
      if (loading || this.store.notFound()) return;
      if (focus !== null && focus.seq !== this.lastFocusSeq) {
        const region = this.host.nativeElement.querySelector<HTMLElement>(`[data-control="${focus.control}"]`);
        const target = region?.matches('input, select, [role="combobox"]') ? region : region?.querySelector<HTMLElement>('input:not(:disabled), select:not(:disabled), button:not(:disabled), [role="combobox"]') ?? region;
        if (target !== null && target !== undefined) { target.focus(); this.lastFocusSeq = focus.seq; this.lastStep = step; }
      } else if (step !== this.lastStep) {
        this.host.nativeElement.querySelector<HTMLElement>('[data-step-heading]')?.focus();
        this.lastStep = step;
      }
    });
  }

  protected fieldError(name: ControlName): string | null {
    this.value();
    const control = this.store.form.controls[name];
    if (!control.invalid || (!control.touched && !control.dirty)) return null;
    // The server-bound message wins (bindFieldErrors stores it on the control); `store.error()` still answers for
    // a mapped pointer only when the payload carried no binding the control could take (defensive — bound
    // pointers land in `server` first).
    const server = control.getError('server') as ServerControlError | undefined;
    if (server !== undefined && server.messageKeys.length > 0) return server.messageKeys[0] ?? null;
    const error = this.store.error();
    if (error !== null && (error.fieldErrors[name]?.length ?? 0) > 0) return this.mapper.fieldMessageKey(error, name);
    return name === 'title' && control.hasError('maxlength') ? 'series.validation.titleMax' : 'series.validation.required';
  }

  protected async submit(): Promise<void> {
    const generation = this.session.generation();
    const [data, params] = this.routeState();
    const key = `${data['mode']}:${params.get('id')}`;
    const outcome = await this.store.submit();
    const [currentData, currentParams] = this.routeState();
    if (this.destroyRef.destroyed || generation !== this.session.generation() || key !== `${currentData['mode']}:${currentParams.get('id')}`) return;
    await this.handleOutcome(outcome, generation);
  }

  protected retryTemplates(): void {
    if (this.templatesRetryAfter() !== null && this.templatesRetryLocked()) return;
    void this.store.goTo(4);
  }

  protected backToList(): void { void this.router.navigate(['/series']); }

  private async handleOutcome(outcome: SubmitOutcome, generation: number): Promise<void> {
    const id = this.store.seriesId();
    const mode = this.store.mode();
    if (outcome.kind === 'created') { await this.router.navigate(['/series', outcome.id]); return; }
    if (outcome.kind === 'reloaded') {
      // "Load latest": the editor stays on /edit with the form replaced by the server state — announced, not a
      // "saved" snackbar, and no navigation.
      const text = await firstValueFrom(this.transloco.selectTranslate<string>('save.reloaded', {}, 'series'));
      if (this.destroyRef.destroyed || generation !== this.session.generation() || id !== this.store.seriesId() || mode !== this.store.mode()) return;
      void this.announcer.announce(text, 'polite').catch(() => undefined);
      return;
    }
    if (outcome.kind === 'saved') {
      const text = await firstValueFrom(this.transloco.selectTranslate<string>('save.saved', {}, 'series'));
      if (this.destroyRef.destroyed || generation !== this.session.generation() || id !== this.store.seriesId() || mode !== this.store.mode()) return;
      this.snackbar.open(text, undefined, { duration: 4000, politeness: 'polite' });
      void this.announcer.announce(text, 'polite').catch(() => undefined);
      if (id !== null) await this.router.navigate(['/series', id]);
      return;
    }
    // A conflict after a conflict is normal (another write landed between the re-read and the retry): the dialog
    // reopens until the resolution is not another conflict.
    while (outcome.kind === 'conflict') {
      if (this.conflictOpen) return;
      this.conflictOpen = true;
      try {
        const ref = this.conflictDialog.open({
          messageKey: this.store.conflictCode() === 'concurrency_conflict' ? 'errors.concurrency_conflict' : 'series.save.conflict',
          saveAsNewLabel: 'errors.conflict.saveAsNew',
          traceId: this.store.conflictTraceId(),
        }, { viewContainerRef: this.viewContainerRef });
        // `afterClosed()` types `undefined` (the dialog closed without a choice); `disableClose: true` makes that
        // unreachable, and a cancel that writes nothing is the safe reading of it.
        const choice = (await firstValueFrom(ref.afterClosed())) ?? 'cancel';
        if (this.destroyRef.destroyed || generation !== this.session.generation() || id !== this.store.seriesId() || mode !== this.store.mode()) return;
        const resolved = await this.store.resolveConflict(choice);
        if (this.destroyRef.destroyed || generation !== this.session.generation() || id !== this.store.seriesId() || mode !== this.store.mode()) return;
        if (resolved === null) return;
        outcome = resolved;
      } finally { this.conflictOpen = false; }
      if (outcome.kind !== 'conflict') await this.handleOutcome(outcome, generation);
    }
  }
}
