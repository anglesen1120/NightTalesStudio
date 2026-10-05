import { DestroyRef, Injectable, computed, inject, isDevMode, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AppErrorMapper, bindFieldErrors, clearServerErrors, messageCodeFor } from '@core/errors';
import type { AppError } from '@core/errors';
import { IdempotencyKeys } from '@core/http';
import type { IdempotencyIntent } from '@core/http';
import { RealtimeService } from '@core/realtime';
import type { RealtimeSubscription } from '@core/realtime/realtime-subscriptions';
import { SessionStore } from '@core/session';
import { EPISODE_GATE } from './episode-gate';
import type { EpisodeDetail, EpisodeGeneration, LengthTier, SeriesSummary } from '../domain/episode.model';
import { isUuid } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';
import { failureKey, isTerminal, stageKey } from '../domain/generation-state';
import { SeriesOptionsRepository } from '../domain/series-options.repository';

function isAppError(value: unknown): value is AppError {
  if (typeof value !== 'object' || value === null) return false;
  const candidate = value as Partial<Record<keyof AppError, unknown>>;
  return typeof candidate.code === 'string' && typeof candidate.status === 'number' &&
    typeof candidate.kind === 'string' && typeof candidate.fieldErrors === 'object';
}

const EMPTY_FIELD_KEYS: readonly string[] = [];
const NON_RETRYABLE_CODES: Readonly<Record<string, true | undefined>> = {
  fact_sheet_required: true,
  idempotency_key_reused: true,
  payload_too_large: true,
  validation_failed: true,
  story_invalid_content: true,
  episode_invalid_state: true,
};

function hasUnknownOutcome(error: AppError): boolean {
  return error.kind === 'network' || error.status === 0 || error.status >= 500;
}

function isNonRetryableCode(code: string): boolean {
  return NON_RETRYABLE_CODES[code] === true;
}

@Injectable()
export class NewEpisodeStore {
  private readonly repo = inject(EpisodesRepository);
  private readonly seriesRepo = inject(SeriesOptionsRepository);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);
  private readonly mapper = inject(AppErrorMapper);
  private readonly keys = inject(IdempotencyKeys);
  private readonly destroyRef = inject(DestroyRef);
  private readonly gate = inject(EPISODE_GATE, { optional: true });
  private readonly ownerGeneration = this.session.generation();

  readonly form = new FormGroup({
    title: new FormControl<string | null>('', { validators: [Validators.maxLength(120)] }),
    brief: new FormControl('', { nonNullable: true, validators: [(control) => Validators.required(control), Validators.maxLength(1000),
      (control) => typeof control.value === 'string' && control.value.trim().length > 0 ? null : { required: true }], }),
    lengthTier: new FormControl<LengthTier>('standard', { nonNullable: true }),
  });
  private readonly briefValue = toSignal(this.form.controls.brief.valueChanges, { initialValue: '' });
  private readonly formStatus = toSignal(this.form.statusChanges, { initialValue: this.form.status });
  readonly briefLength = computed(() => this.briefValue().length);
  private readonly seriesState = signal<SeriesSummary | null>(null);
  readonly series = this.seriesState.asReadonly();
  private readonly seriesFailedState = signal(false);
  readonly seriesFailed = this.seriesFailedState.asReadonly();
  readonly isTrueCrime = computed(() => this.series()?.topic === 'true_crime');
  private readonly phaseState = signal<'form' | 'submitting' | 'generating' | 'failed' | 'paused' | 'handoff'>('form');
  readonly phase = this.phaseState.asReadonly();
  private readonly submittingState = signal(false);
  readonly submitting = this.submittingState.asReadonly();
  private readonly errorState = signal<string | null>(null);
  readonly errorKey = this.errorState.asReadonly();
  private readonly traceState = signal<string | null>(null);
  readonly traceId = this.traceState.asReadonly();
  private readonly unmappedState = signal<readonly string[]>(EMPTY_FIELD_KEYS);
  readonly unmappedFieldKeys = this.unmappedState.asReadonly();
  private readonly createLostState = signal(false);
  readonly createLost = this.createLostState.asReadonly();
  private readonly factSheetState = signal(false);
  readonly factSheetRequired = this.factSheetState.asReadonly();
  private readonly episodeState = signal<string | null>(null);
  readonly episodeId = this.episodeState.asReadonly();
  private readonly detailState = signal<EpisodeDetail | null>(null);
  readonly detail = this.detailState.asReadonly();
  private readonly generationState = signal<EpisodeGeneration | null>(null);
  readonly generation = this.generationState.asReadonly();
  readonly stageKey = computed(() => stageKey(this.generation()?.stage));
  readonly failureKey = computed(() => {
    if (this.phase() !== 'failed' || !isTerminal(this.generation()?.status) || this.generation()?.status === 'succeeded') return null;
    const code = this.generation()?.errorCode;
    return code ? failureKey(code) ?? `errors.${messageCodeFor(code)}` : 'story.generation.error.story_generation_failed';
  });
  readonly paused = computed(() => this.generation()?.processingPaused ?? null);
  private readonly retryAfterState = signal<number | null>(null);
  readonly retryAfterSec = this.retryAfterState.asReadonly();
  readonly gateBlocked = computed(() => this.gate !== null && this.gate() !== 'open');
  private readonly retryAllowed = signal(false);
  readonly canRetry = computed(() => this.retryAllowed() && this.phase() === 'failed' && !this.submitting() &&
    this.retryAfterSec() === null && !this.gateBlocked() && !this.factSheetRequired() && this.session.generation() === this.ownerGeneration);
  readonly canSubmit = computed(() => this.formStatus() === 'VALID' && this.series() !== null && !this.seriesFailed() &&
    this.phase() === 'form' && !this.submitting() && this.retryAfterSec() === null && !this.gateBlocked() && this.session.generation() === this.ownerGeneration);

  private seriesId: string | null = null;
  private intent: IdempotencyIntent | null = null;
  private subscription: RealtimeSubscription | null = null;
  private destroyed = false;
  private commandToken = 0;
  private seriesToken = 0;
  private refetchToken = 0;
  private generateOutcome: 'idle' | 'pending' | 'unknown' | 'known' = 'idle';
  private refetchInFlight: { id: string; commandToken: number; promise: Promise<void> } | null = null;
  private refetchQueued = false;
  private seriesLoading = false;

  constructor() {
    this.form.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      clearServerErrors(this.form);
      if (this.unmappedState().length > 0) this.unmappedState.set(EMPTY_FIELD_KEYS);
    });
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.commandToken++;
      this.seriesToken++;
      this.refetchToken++;
      this.subscription?.unsubscribe();
    });
  }

  init(seriesId: string): void {
    if (this.seriesId !== null || this.destroyed) return;
    this.seriesId = seriesId;
    void this.reloadSeries();
  }

  async reloadSeries(): Promise<void> {
    const id = this.seriesId;
    if (id === null || this.destroyed || this.seriesLoading || this.session.generation() !== this.ownerGeneration) return;
    if (!isUuid(id)) {
      this.seriesFailedState.set(true);
      this.errorState.set('errors.not_found');
      return;
    }
    const generation = this.session.generation();
    const token = ++this.seriesToken;
    this.seriesLoading = true;
    try {
      const series = await this.seriesRepo.get(id);
      if (!this.isCurrent(generation, token, this.seriesToken)) return;
      this.seriesState.set(series);
      this.seriesFailedState.set(false);
      this.errorState.set(null);
      this.traceState.set(null);
    } catch (cause) {
      if (!this.isCurrent(generation, token, this.seriesToken)) return;
      this.seriesState.set(null);
      this.seriesFailedState.set(true);
      this.showReadError(cause);
    } finally {
      this.seriesLoading = false;
    }
  }

  async create(): Promise<void> {
    if (this.destroyed || this.session.generation() !== this.ownerGeneration || this.submitting() || this.episodeId() !== null) return;
    clearServerErrors(this.form);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    if (!this.canSubmit() || this.seriesId === null) return;
    const generation = this.session.generation();
    const token = ++this.commandToken;
    this.clearFailure();
    this.submittingState.set(true);
    this.phaseState.set('submitting');
    try {
      const values = this.form.getRawValue();
      const created = await this.repo.create(this.seriesId, { ...values, brief: values.brief.trim(), title: values.title?.trim() || null });
      if (!this.isCurrent(generation, token, this.commandToken)) return;
      this.episodeState.set(created.id);
      this.subscription = this.realtime.subscribeEpisode(created.id, () => {
        if (this.isCurrent(generation, token, this.commandToken)) void this.refetch(created.id, true);
      });
      if (this.isTrueCrime()) {
        await this.handoff(created.id, generation, token, true);
      } else {
        this.intent = this.keys.begin('story.generate');
        await this.generate(created.id, generation, token);
      }
    } catch (cause) {
      if (!this.isCurrent(generation, token, this.commandToken)) return;
      const error = this.showError(cause);
      this.createLostState.set(hasUnknownOutcome(error));
      this.phaseState.set('form');
    } finally {
      if (this.isCurrent(generation, token, this.commandToken)) this.submittingState.set(false);
    }
  }

  async retry(): Promise<void> {
    const id = this.episodeId();
    if (this.destroyed || id === null || !this.canRetry()) return;
    const generation = this.session.generation();
    const token = ++this.commandToken;
    this.refetchToken++;
    this.generationState.set(null);
    // Replace the registration's command guard for this new attempt, still before the POST.
    this.subscription?.unsubscribe();
    this.subscription = this.realtime.subscribeEpisode(id, () => {
      if (this.isCurrent(generation, token, this.commandToken)) void this.refetch(id, true);
    });
    if (this.intent === null || this.intent.isSettled) this.intent = this.keys.begin('story.generate');
    this.clearFailure();
    this.submittingState.set(true);
    try {
      await this.generate(id, generation, token);
    } finally {
      if (this.isCurrent(generation, token, this.commandToken)) this.submittingState.set(false);
    }
  }

  countdownElapsed(): void {
    this.retryAfterState.set(null);
  }

  private async generate(id: string, generation: number, token: number): Promise<void> {
    const intent = this.intent;
    if (intent === null || !this.isCurrent(generation, token, this.commandToken)) return;
    this.phaseState.set('generating');
    this.generateOutcome = 'pending';
    try {
      await this.repo.generateStory(id, intent);
      if (!this.isCurrent(generation, token, this.commandToken)) return;
      this.generateOutcome = 'known';
      intent.settle();
      await this.refetch(id);
    } catch (cause) {
      if (!this.isCurrent(generation, token, this.commandToken)) return;
      if (this.phase() === 'handoff') return;
      const error = isAppError(cause) ? cause : this.mapper.from(cause);
      const unknownOutcome = hasUnknownOutcome(error);
      this.generateOutcome = unknownOutcome ? 'unknown' : 'known';
      if (!unknownOutcome) intent.settle();
      if (error.code === 'story_generation_in_progress') {
        this.errorState.set(null);
        await this.refetch(id);
        return;
      }
      this.showError(error);
      this.phaseState.set('failed');
      this.retryAllowed.set(unknownOutcome || (error.status >= 400 && error.status < 500 && !isNonRetryableCode(error.code)));
      if (error.code === 'episode_invalid_state') await this.refetch(id);
    }
  }

  private refetch(id: string, invalidation = false): Promise<void> {
    if (this.destroyed || this.session.generation() !== this.ownerGeneration || this.episodeId() !== id || this.phase() === 'handoff') return Promise.resolve();
    if (this.refetchInFlight?.id === id) {
      if (invalidation || this.refetchInFlight.commandToken !== this.commandToken) this.refetchQueued = true;
      return this.refetchInFlight.promise;
    }
    // Events during a POST are covered by its seeding read; they must not restore an older run.
    if (this.generateOutcome === 'pending') return Promise.resolve();
    this.refetchQueued = false;
    const promise = this.readEpisode(id).finally(() => {
      if (this.refetchInFlight?.promise !== promise) return;
      this.refetchInFlight = null;
      if (this.refetchQueued) {
        this.refetchQueued = false;
        void this.refetch(id);
      }
    });
    this.refetchInFlight = { id, commandToken: this.commandToken, promise };
    return promise;
  }

  private async readEpisode(id: string): Promise<void> {
    const generation = this.session.generation();
    const token = ++this.refetchToken;
    try {
      const detail = await this.repo.get(id);
      if (!this.isCurrent(generation, token, this.refetchToken) || this.episodeId() !== id || this.generateOutcome === 'pending' || this.refetchQueued) return;
      this.detailState.set(detail);
      this.generationState.set(detail.generation);
      const run = detail.generation;
      if (detail.status === 'story_review' || (run?.status === 'succeeded' && detail.hasCurrentStory)) {
        await this.handoff(id, generation, this.commandToken, false);
      } else if (run?.processingPaused) {
        this.phaseState.set('paused');
        this.clearFailure();
      } else if (isTerminal(run?.status) && run?.status !== 'succeeded') {
        // A previous failed run cannot establish the outcome of an unknown POST from this intent.
        if (this.generateOutcome === 'known') this.intent?.settle();
        this.clearFailure();
        this.phaseState.set('failed');
        this.retryAllowed.set(true);
      } else if (run?.status === 'running' || run?.status === 'queued') {
        // Observing a live run resolves an unknown POST; its later failure is a new-attempt boundary.
        if (this.generateOutcome === 'unknown') {
          this.generateOutcome = 'known';
          this.intent?.settle();
        }
        this.clearFailure();
        this.phaseState.set('generating');
      }
    } catch (cause) {
      if (this.isCurrent(generation, token, this.refetchToken) && this.generateOutcome !== 'pending') this.showReadError(cause);
    }
  }

  private async handoff(id: string, generation: number, token: number, research: boolean): Promise<void> {
    this.clearFailure();
    this.phaseState.set('handoff');
    let navigated = false;
    try {
      navigated = await this.router.navigate(research ? ['/episodes', id, 'research'] : ['/episodes', id]);
    } catch {
      // Navigation failure is not a create/network failure: the episode is already known to exist.
    }
    if (navigated || !this.isCurrent(generation, token, this.commandToken)) return;
    this.phaseState.set('failed');
    this.errorState.set('errors.unknown_error');
    // True crime retains a research action; fiction retains its open-episode action and can resync again.
    this.factSheetState.set(research);
  }

  private showReadError(cause: unknown): void {
    const error = isAppError(cause) ? cause : this.mapper.from(cause);
    this.errorState.set(this.mapper.messageKey(error));
    this.traceState.set(error.traceId ?? null);
  }

  private clearFailure(): void {
    this.errorState.set(null);
    this.traceState.set(null);
    if (this.unmappedState().length > 0) this.unmappedState.set(EMPTY_FIELD_KEYS);
    this.createLostState.set(false);
    this.factSheetState.set(false);
    this.retryAllowed.set(false);
  }

  private showError(cause: unknown): AppError {
    const error = isAppError(cause) ? cause : this.mapper.from(cause);
    this.errorState.set(this.mapper.messageKey(error));
    this.traceState.set(error.traceId ?? null);
    if (error.code === 'validation_failed' || error.code === 'story_invalid_content' || error.code === 'payload_too_large') {
      const unmapped = bindFieldErrors(this.form, error, { markTouched: true });
      this.unmappedState.set([...new Set(unmapped.map((pointer) => this.mapper.fieldMessageKey(error, pointer)))]);
    }
    if (error.status === 429) this.retryAfterState.set(error.retryAfterSec ?? null);
    if (error.code === 'fact_sheet_required') this.factSheetState.set(true);
    if (error.code === 'idempotency_key_reused') {
      this.errorState.set('errors.unknown_error');
      if (isDevMode()) {
        console.error('Story generation idempotency intent was rejected', error.traceId ?? '');
      }
    }
    return error;
  }

  private isCurrent(generation: number, token: number, latest: number): boolean {
    return !this.destroyed && token === latest && this.session.generation() === generation;
  }
}
