import { LiveAnnouncer } from '@angular/cdk/a11y';
import { Injectable, computed, inject, signal } from '@angular/core';
import { TranslocoService, provideTranslocoScope } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import type { Language, Voice } from '../domain/voice.model';
import { CatalogRepository } from './catalog.repository';

/** The scopes the store's announcement and the voice rows read; `series.routes.ts` registers them. */
export const VOICE_STEP_TRANSLATION_SCOPES = provideTranslocoScope('series', 'voices');

/** Verbatim copy key (plan 10.4 §6.2); a constant, so the key scan attributes it to the picker template that also renders it. */
const CLEARED_BY_LANGUAGE_KEY = 'series.voice.clearedByLanguage';

interface CachedList {
  readonly etag: string | null;
  readonly voices: readonly Voice[];
}

/**
 * State of the series editor's voice step (10.4.2-T2, README D2: application layer, page-scoped `@Injectable()` -
 * the editor provides it, never `providedIn: 'root'`).
 *
 * - `load(language)` is single-flight per language: a second call for the language being loaded returns the same
 *   promise (one repository call). The previous list stays visible while another language loads (no empty flash).
 * - A response is applied only if its language is still current, no newer `load` for another language started, and the
 *   session did not end meanwhile (generation tokens); otherwise it is discarded silently.
 * - A language switch clears the selection (U6) and announces `series.voice.clearedByLanguage` politely.
 * - The ETag of each language stays private to this store; a 304 re-uses that language's cached list without
 *   toggling `loading`.
 * - `validation_failed` is a field error, every other failure is `error`; both keep the last good list.
 */
@Injectable()
export class VoiceStepStore {
  private readonly repository = inject(CatalogRepository);
  private readonly session = inject(SessionStore);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly transloco = inject(TranslocoService);

  private readonly inFlight = new Map<Language, Promise<void>>();
  private readonly cache = new Map<Language, CachedList>();
  private generation = 0;

  readonly language = signal<Language>('vi');
  readonly voices = signal<readonly Voice[]>([]);
  readonly loading = signal(false);
  readonly error = signal<AppError | null>(null);
  /** `errors.validation_failed` while the server rejected the language; bound to the language control by the editor. */
  readonly fieldError = signal<string | null>(null);
  /** Seconds to wait after a 429 (from `Retry-After`), `null` otherwise; feeds `RetryCountdown`. */
  readonly retryAfterSec = signal<number | null>(null);
  readonly selectedId = signal<string | null>(null);
  readonly selected = computed(() => this.voices().find((voice) => voice.id === this.selectedId()) ?? null);
  readonly compatible = computed(() => {
    const selected = this.selected();
    return selected === null || selected.language === this.language();
  });

  load(language: Language): Promise<void> {
    if (language !== this.language()) {
      this.language.set(language);
      this.generation += 1;
      // Requests of the old language are stale now; switching back must start a fresh one, not join them.
      this.inFlight.clear();
      if (this.selectedId() !== null) {
        this.clear();
        void this.announceCleared();
      }
    }
    const running = this.inFlight.get(language);
    if (running !== undefined) return running;
    const request = this.run(language).finally(() => {
      if (this.inFlight.get(language) === request) this.inFlight.delete(language);
    });
    this.inFlight.set(language, request);
    return request;
  }

  /** Re-issues the load for the current language (error retry, countdown elapsed). */
  retry(): Promise<void> {
    return this.load(this.language());
  }

  /** Selects a voice of the language last loaded; an unknown id or a voice of another language is ignored. */
  select(id: string): void {
    const voice = this.voices().find((candidate) => candidate.id === id);
    if (voice === undefined || voice.language !== this.language()) return;
    this.selectedId.set(id);
  }

  clear(): void {
    this.selectedId.set(null);
  }

  private async run(language: Language): Promise<void> {
    const generation = this.generation;
    const sessionGeneration = this.session.generation();
    const cached = this.cache.get(language);
    this.loading.set(true);
    this.error.set(null);
    this.fieldError.set(null);
    this.retryAfterSec.set(null);
    const result = await this.repository.voices(language, cached?.etag ?? null);
    if (generation !== this.generation || language !== this.language() || sessionGeneration !== this.session.generation()) {
      // A late answer: whoever superseded it owns `loading`.
      if (generation === this.generation && language === this.language()) this.loading.set(false);
      return;
    }
    this.loading.set(false);
    if (!result.ok) {
      this.fail(result.error);
      return;
    }
    if (result.value.kind === 'notModified') {
      if (cached !== undefined) this.voices.set(cached.voices);
      return;
    }
    this.cache.set(language, { etag: result.value.etag, voices: result.value.voices });
    this.voices.set(result.value.voices);
    if (!this.compatible()) this.clear();
  }

  private fail(error: AppError): void {
    if (error.code === 'validation_failed') {
      this.fieldError.set('errors.validation_failed');
      return;
    }
    this.error.set(error);
    this.retryAfterSec.set(error.kind === 'rate_limit' ? (error.retryAfterSec ?? null) : null);
  }

  private async announceCleared(): Promise<void> {
    const message = await firstValueFrom(this.transloco.selectTranslate<string>(CLEARED_BY_LANGUAGE_KEY));
    await this.announcer.announce(message, 'polite');
  }
}
