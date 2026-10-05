import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';
import { AppErrorMapper } from '@core/errors';
import type { AppError } from '@core/errors';
import { SessionStore } from '@core/session';
import type { EpisodeDetail } from '../domain/episode.model';
import { EpisodesRepository } from '../domain/episodes.repository';

export function isAppError(value: unknown): value is AppError {
  if (typeof value !== 'object' || value === null) return false;
  const error = value as Partial<AppError>;
  return typeof error.code === 'string' && typeof error.status === 'number' && typeof error.kind === 'string' && typeof error.fieldErrors === 'object';
}

/** Page-owned detail state. Background reads and user reloads share the same generation fence. */
@Injectable()
export class EpisodeDetailStore {
  private readonly repo = inject(EpisodesRepository);
  private readonly mapper = inject(AppErrorMapper);
  private readonly session = inject(SessionStore);
  private readonly ownerGeneration = this.session.generation();
  private readonly state = signal<EpisodeDetail | null>(null);
  readonly episode = this.state.asReadonly();
  readonly detail = this.episode;
  readonly loading = signal(false);
  readonly error = signal<AppError | null>(null);
  readonly nextActions = computed(() => this.episode()?.nextActions ?? []);
  private id: string | null = null;
  private generation = 0;
  private request = 0;
  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => { this.destroyed = true; this.generation++; this.request++; });
    effect(() => {
      if (this.session.generation() !== this.ownerGeneration) {
        this.generation++; this.request++; this.id = null;
        this.state.set(null); this.error.set(null); this.loading.set(false);
      }
    });
  }

  async load(id: string): Promise<EpisodeDetail | null> {
    if (!this.active()) return null;
    this.id = id; this.generation++; this.request++;
    this.state.set(null); this.error.set(null);
    return this.refetch({ userAction: true });
  }

  async refetch(opts?: { userAction?: boolean }): Promise<EpisodeDetail | null> {
    if (!this.active() || this.id === null) return null;
    const id = this.id, generation = this.generation, request = ++this.request;
    this.loading.set(true);
    try {
      const detail = await this.repo.get(id, opts);
      if (!this.active() || generation !== this.generation || request !== this.request) return null;
      this.state.set(detail); this.error.set(null);
      return detail;
    } catch (cause) {
      if (this.active() && generation === this.generation && request === this.request) this.error.set(isAppError(cause) ? cause : this.mapper.from(cause));
      return null;
    } finally {
      if (this.active() && generation === this.generation && request === this.request) this.loading.set(false);
    }
  }

  private active(): boolean { return !this.destroyed && this.session.generation() === this.ownerGeneration; }
}
